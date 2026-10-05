using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// File-based cache for generated MP4 video-audio files.
/// Caches ffmpeg-generated MP4s so subsequent plays of the same item
/// (with same album art) are served instantly without re-encoding.
/// Includes per-item locking to prevent concurrent ffmpeg processes
/// for the same cache key and avoid serving partially-written files.
/// </summary>
public class VideoAudioCache
{
    private const string CacheSubDir = "alexaskill-video-audio";

    /// <summary>
    /// Name of the transient subtree under the cache root (JF-537.1): oversize
    /// transcode encodes write their HLS generation directories inside it instead
    /// of the capped cache root. The name is load-bearing: it carries NO
    /// underscore, so neither top-directory-only enumeration of the eviction
    /// sweep (<c>*_*</c> generation dirs, <c>*.mp4</c> files) can ever match it,
    /// and it holds no top-level playlist. Transient bytes therefore never count
    /// toward the cap and the cap sweep never deletes them; the idle reaper
    /// (<see cref="ReapIdleTransientEntries"/>) owns their lifecycle.
    /// </summary>
    private const string TransientSubDirName = "transient";

    /// <summary>
    /// Default cache cap in MB when the plugin configuration is unavailable (the
    /// <c>?? </c> fallback of <see cref="EffectiveCacheCapMB"/> and
    /// <see cref="EvictIfNeededCore"/>). Single definition so the JF-537 oversize
    /// decision and the eviction sweep can never disagree about the default they read.
    /// </summary>
    internal const int DefaultCacheCapMB = 4096;

    private readonly ILogger<VideoAudioCache> _logger;
    private readonly string _cacheDir;
    private readonly string _transientDir;

    /// <summary>
    /// Per-item locks keyed by cache file path. Prevents concurrent ffmpeg
    /// processes for the same item while allowing different items to generate
    /// in parallel. Uses reference counting to clean up SemaphoreSlim objects
    /// when no longer needed (same pattern as Jellyfin's TranscodeManager).
    /// </summary>
    private readonly ConcurrentDictionary<string, RefCountedLock> _itemLocks = new();

    /// <summary>
    /// In-memory last-served timestamps keyed by cache entry path, so LRU eviction reflects real
    /// recency of use independent of filesystem mount options (relatime/noatime suppress atime).
    /// Falls back to filesystem atime for entries with no recorded serve (e.g. created before this
    /// process started). Doubles as the playback-pin source (see
    /// <see cref="PlaybackEvictionExemptionTtl"/>): every playlist and segment fetch refreshes the
    /// entry's timestamp, so an entry being watched right now is never the eviction victim.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessUtc = new();

    /// <summary>
    /// JF-498 review C1 (playback pin): how long a recorded serve exempts a cache entry
    /// from eviction. A playing client fetches segments every few seconds and the
    /// playlist every few tens of seconds, so a timestamp inside this window means the
    /// entry is being watched right now; deleting it mid-playback stalls the stream
    /// with 404s and forces a from-zero re-encode. The JF-428 write pin only covers
    /// the encode window (it releases when ffmpeg exits, minutes into a multi-hour
    /// watch); serve recency is what protects the completed-but-being-watched entry.
    /// Ten minutes is far above any client fetch interval, so the exemption only
    /// expires after the client actually stops. An entry alone exceeding the cap while
    /// exempt is tolerated: disk temporarily over budget beats breaking playback (the
    /// sweep still evicts every older non-exempt entry and warns about the remainder).
    /// Internal test hook (the InternalsVisibleTo seam): shrink it to milliseconds to
    /// exercise TTL expiry without wall-clock sleeps.
    /// </summary>
    internal TimeSpan PlaybackEvictionExemptionTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// JF-537.1: how long after the last liveness signal (a recorded playlist or
    /// segment serve, else the directory's latest file write) a TRANSIENT-root
    /// oversize encode directory may linger before the idle reaper deletes it.
    /// VideoApp emits no playback-stop event, so segment-fetch recency is the only
    /// observable liveness signal (the platform fact that made deletion semantics
    /// the fifth point of the JF-537 no-cache rejection). 30 minutes = 3x the
    /// playback-pin window: far above any live client fetch cadence, sized to
    /// survive a long pause mid-watch, while bounding disk hold to roughly the
    /// watched entry plus entries idle within the window. There is deliberately
    /// NO byte budget on the transient root: qualification means the entry alone
    /// exceeds the cache cap, so any cap-derived budget would reap idle entries
    /// immediately and collapse this TTL (and the replay benefit) to zero.
    /// Internal test hook (the InternalsVisibleTo seam): shrink it to
    /// milliseconds to exercise reaping without wall-clock sleeps.
    /// </summary>
    internal TimeSpan TransientIdleReapTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Record that a cache entry was served, for in-memory LRU eviction (JF-320 part 2).</summary>
    private void RecordAccess(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _lastAccessUtc[path] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Entries currently being WRITTEN (JF-428): an HLS directory mid-encode, an mp4
    /// being produced, or a faststart remux in flight. Eviction never deletes a pinned
    /// entry, even when it is the oldest over-limit one: deleting the segments the Echo
    /// Show is fetching mid-book stalls playback with 404s. Scope is the WRITE window
    /// only; after the write the entry is an ordinary LRU citizen, and post-encode
    /// streaming is protected by the playback pin (serve recency inside
    /// <see cref="PlaybackEvictionExemptionTtl"/>, recorded by <see cref="RecordAccess"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _pinnedPaths = new();

    /// <summary>
    /// Mark a cache entry as in-use (refcounted); eviction skips it while any pin is
    /// held. Refcounted because a retrying encode can re-pin the same path while the
    /// previous attempt's delayed exit-poll release (up to 500ms) is still pending.
    /// </summary>
    public void Pin(string path) => _pinnedPaths.AddOrUpdate(path, 1, (_, count) => count + 1);

    /// <summary>
    /// Internal test seam (InternalsVisibleTo): whether <paramref name="path"/> holds
    /// at least one <see cref="Pin"/> right now. Lets controller-level tests prove a
    /// newly written file landed INSIDE the pin window (the JF-428/JF-536 ordering:
    /// the pre-written playlist must never exist unpinned while its encode runs).
    /// </summary>
    /// <param name="path">The cache entry path (file or HLS directory).</param>
    /// <returns>True while any pin is held.</returns>
    internal bool IsPinned(string path) => _pinnedPaths.ContainsKey(path);

    /// <summary>Release one pin taken by <see cref="Pin"/>; the entry becomes evictable when the last pin is released. Unpinning a path that was never pinned is a no-op.</summary>
    public void Unpin(string path)
    {
        while (true)
        {
            if (!_pinnedPaths.TryGetValue(path, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                // Remove only if still the value we observed (atomic compare-and-remove)
                if (_pinnedPaths.TryRemove(new KeyValuePair<string, int>(path, count)))
                {
                    return;
                }
            }
            else if (_pinnedPaths.TryUpdate(path, count - 1, count))
            {
                return;
            }

            // Raced with another Pin/Unpin: re-read and retry
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoAudioCache"/> class.
    /// </summary>
    /// <param name="appPaths">Jellyfin application paths for locating the cache directory.</param>
    /// <param name="logger">Logger instance.</param>
    public VideoAudioCache(IApplicationPaths appPaths, ILogger<VideoAudioCache> logger)
    {
        _logger = logger;
        _cacheDir = Path.Combine(appPaths.CachePath, CacheSubDir);
        _transientDir = Path.Combine(_cacheDir, TransientSubDirName);
    }

    /// <summary>
    /// Gets the cache directory path (exposed for testing).
    /// </summary>
    internal string CacheDir => _cacheDir;

    /// <summary>
    /// The cap the eviction sweep enforces right now, in MB (JF-537): the configured
    /// <see cref="Configuration.PluginConfiguration.VideoAudioCacheSizeMB"/>, or the
    /// default when the plugin instance is not reachable (tests, early startup). The
    /// controller reads this for the oversize-encode decision so that decision and the
    /// sweep always compare against the same number.
    /// </summary>
    internal int EffectiveCacheCapMB => Plugin.Instance?.Configuration.VideoAudioCacheSizeMB ?? DefaultCacheCapMB;

    /// <summary>
    /// Minimum valid cache file size in bytes. Files smaller than this are treated as
    /// corrupt stubs (e.g. from an interrupted ffmpeg run) and are deleted + treated as cache misses.
    /// A real MP4 with even 1s of audio is at least ~10 KB.
    /// </summary>
    private const long MinValidFileSize = 10 * 1024;

    /// <summary>
    /// Returns the cached MP4 file if it exists and has valid size, null otherwise.
    /// Prefers the seekable faststart version (suffix <c>.fs.mp4</c>) over the fragmented version.
    /// Files smaller than <see cref="MinValidFileSize"/> are treated as cache misses
    /// but are NOT deleted here — they may be actively being written by another request.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified for cache key invalidation.</param>
    /// <returns>Cached file info or null if not cached.</returns>
    public Task<FileInfo?> GetCachedFile(string itemId, long artModifiedTicks)
    {
        // Prefer seekable faststart version if available
        string fsPath = GetFaststartCacheFilePath(itemId, artModifiedTicks);
#pragma warning disable CA3003
        var fsFi = new FileInfo(fsPath);
#pragma warning restore CA3003
        if (fsFi.Exists && fsFi.Length >= MinValidFileSize)
        {
            _logger.LogDebug("VideoAudio cache hit (faststart): {Path} ({Size} bytes)", fsPath, fsFi.Length);
            RecordAccess(fsFi.FullName);
            return Task.FromResult<FileInfo?>(fsFi);
        }

        // Fall back to fragmented version
        string path = GetCacheFilePath(itemId, artModifiedTicks);
#pragma warning disable CA3003 // itemId is validated as GUID by the caller (VideoAudioController)
        var fi = new FileInfo(path);
#pragma warning restore CA3003

        if (fi.Exists && fi.Length >= MinValidFileSize)
        {
            _logger.LogDebug("VideoAudio cache hit (fragmented): {Path} ({Size} bytes)", path, fi.Length);
            RecordAccess(fi.FullName);
            return Task.FromResult<FileInfo?>(fi);
        }

        _logger.LogDebug("VideoAudio cache miss: {Path}", path);
        return Task.FromResult<FileInfo?>(null);
    }

    /// <summary>
    /// Delete a corrupt stub file for the given item. Only call this while holding the
    /// per-item lock (via <see cref="LockItemAsync"/>) to avoid deleting a file that
    /// another request is actively writing.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    public void DeleteStubIfPresent(string itemId, long artModifiedTicks)
    {
        string path = GetCacheFilePath(itemId, artModifiedTicks);
#pragma warning disable CA3003
        var fi = new FileInfo(path);
#pragma warning restore CA3003

        if (fi.Exists && fi.Length < MinValidFileSize)
        {
            _logger.LogWarning("VideoAudio cache stub detected ({Size} bytes), deleting: {Path}", fi.Length, path);
            try
            {
                fi.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to delete cache stub: {Path}", path);
            }
        }
    }

    /// <summary>
    /// Acquire a per-item lock for the given cache key. Returns an <see cref="IDisposable"/>
    /// that releases the lock when disposed. Use with <c>using</c>.
    /// Multiple concurrent requests for the same item will serialize here; different items
    /// proceed in parallel.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>An async disposable that releases the lock on dispose.</returns>
    public async Task<IDisposable> LockItemAsync(string itemId, long artModifiedTicks)
    {
        string key = GetCacheFilePath(itemId, artModifiedTicks);
        while (true)
        {
            var rc = _itemLocks.GetOrAdd(key, _ => new RefCountedLock());
            Interlocked.Increment(ref rc.RefCount);
            try
            {
                await rc.Semaphore.WaitAsync().ConfigureAwait(false);
                return new Releaser(key, this);
            }
            catch (ObjectDisposedException)
            {
                // Raced with a ReleaseLock that decremented this entry to 0 and disposed its
                // semaphore between our GetOrAdd and WaitAsync. Loop to acquire a fresh entry (JF-320).
                continue;
            }
        }
    }

    /// <summary>
    /// Returns the expected cache file path for the given item and art modification ticks.
    /// Format: {cacheDir}/{itemId}_{artModifiedTicks}.mp4
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Full path to the expected cache file.</returns>
    public string GetCacheFilePath(string itemId, long artModifiedTicks)
    {
        return Path.Combine(_cacheDir, $"{itemId}_{artModifiedTicks}.mp4");
    }

    /// <summary>
    /// Returns the path for the seekable faststart version of the cached file.
    /// Format: {cacheDir}/{itemId}_{artModifiedTicks}.fs.mp4
    /// This is a separate file from the fragmented version — no overwriting.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Full path to the faststart cache file.</returns>
    public string GetFaststartCacheFilePath(string itemId, long artModifiedTicks)
    {
        return Path.Combine(_cacheDir, $"{itemId}_{artModifiedTicks}.fs.mp4");
    }

    /// <summary>
    /// In-memory cache mapping itemId to HLS directory path.
    /// Avoids filesystem scanning on every segment request (~45-225 per playback).
    /// Populated when HLS generation completes, cleaned up on eviction.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _hlsDirLookup = new();

    /// <summary>
    /// Register the CACHE-root HLS directory path for an item so segment lookups
    /// are O(1). Called after ffmpeg finishes generating the HLS playlist and
    /// segments. Cache-rooted paths only; a transient encode registers its own
    /// resolved directory through <see cref="RegisterHlsDirectoryPath"/> (JF-537.1).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    public void RegisterHlsDirectory(string itemId, long artModifiedTicks)
    {
        RegisterHlsDirectoryPath(itemId, GetHlsDirectoryPath(itemId, artModifiedTicks));
    }

    /// <summary>
    /// Register an explicitly resolved HLS directory path (cache root or
    /// transient root, JF-537.1) for an item so segment lookups are O(1). The
    /// transient encode path uses this to register its transient-root directory;
    /// the value is an opaque path to the lookup, so segment resolution needs no
    /// root awareness of its own.
    /// </summary>
    /// <param name="itemId">The cache key (Jellyfin item or variant key).</param>
    /// <param name="hlsDirPath">The resolved HLS directory path being encoded into.</param>
    public void RegisterHlsDirectoryPath(string itemId, string hlsDirPath)
    {
        _hlsDirLookup[itemId] = hlsDirPath;
    }

    /// <summary>
    /// Clean up a corrupt/partial HLS directory from a previous failed generation.
    /// Only called inside the per-item lock to avoid racing with active generation.
    /// Deletes the directory only if the playlist file is missing or empty (0 bytes),
    /// which indicates ffmpeg never successfully wrote a segment. Since JF-537.1 the
    /// check covers BOTH roots' (itemId, ticks) directories: a generation of the
    /// same key can sit in the cache root (a previous under-cap encode) or the
    /// transient root (a previous oversize encode), and a stub in either is debris.
    /// Safe by contract against valid entries of either root: a non-empty playlist
    /// is never touched here.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    public void CleanupHlsStub(string itemId, long artModifiedTicks)
    {
        foreach (string dirPath in HlsGenerationDirPaths(itemId, artModifiedTicks))
        {
            CleanupHlsStubInDir(dirPath);
        }
    }

    /// <summary>
    /// The per-directory core of <see cref="CleanupHlsStub"/>: delete one
    /// generation directory when its playlist is missing or empty.
    /// </summary>
    /// <param name="dirPath">The generation directory path (cache or transient root).</param>
    private void CleanupHlsStubInDir(string dirPath)
    {
#pragma warning disable CA3003
        if (!Directory.Exists(dirPath))
        {
            return;
        }

        string playlistPath = Path.Combine(dirPath, "stream.m3u8");
        if (!File.Exists(playlistPath))
        {
            _logger.LogWarning("VideoAudio HLS: removing stale directory (no playlist): {Path}", dirPath);
            try { Directory.Delete(dirPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Failed to delete stale HLS directory: {Path}", dirPath); }
            return;
        }

        var fi = new FileInfo(playlistPath);
        if (fi.Length == 0)
        {
            _logger.LogWarning("VideoAudio HLS: removing stub directory (empty playlist): {Path}", dirPath);
            try { Directory.Delete(dirPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Failed to delete HLS stub directory: {Path}", dirPath); }
        }
#pragma warning restore CA3003
    }

    /// <summary>
    /// Delete the playlist and segment files of one HLS generation directory,
    /// best-effort (JF-498 review I1). Unlike <see cref="CleanupHlsStub"/> (which only
    /// removes directories whose playlist is missing or empty) and <see cref="Cleanup"/>
    /// (whose recursive directory delete is all-or-nothing and swallows
    /// IOException/UnauthorizedAccessException),
    /// this targets the individual FILES of a directory whose non-empty playlist
    /// survived those cleanups (a locked or permission-denied recursive delete): the
    /// episode encode runs ffmpeg with <c>append_list</c>, which would otherwise append
    /// the new encode's entries to the stale playlist's entries and bake a doubled
    /// playlist into the cache. Per-file deletion also removes everything deletable
    /// when one undeletable file would have failed the whole recursive delete.
    /// DIR-SCOPED since JF-537.1 (was key-scoped): the caller passes the exact
    /// directory its encode is about to write into (cache root or transient root),
    /// so a same-key VALID entry in the other root is never destroyed before a
    /// transient re-encode. Only call while holding the per-item lock. Failures are
    /// logged as warnings and swallowed: the encode proceeds degraded (stale entries
    /// may survive) rather than failing the play.
    /// </summary>
    /// <param name="hlsDirPath">The HLS generation directory the encode targets.</param>
    public void DeleteHlsEncodeDebris(string hlsDirPath)
    {
#pragma warning disable CA3003 // paths are GUID-derived by the caller (VideoAudioController)
        if (!Directory.Exists(hlsDirPath))
        {
            return;
        }

        string playlistPath = Path.Combine(hlsDirPath, "stream.m3u8");
        try
        {
            File.Delete(playlistPath); // no-op when absent (fresh encode)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete stale HLS playlist before re-encode (append_list may append to its entries): {Path}", playlistPath);
        }

        try
        {
            foreach (string segmentPath in Directory.EnumerateFiles(hlsDirPath, "seg_*.ts"))
            {
                try
                {
                    File.Delete(segmentPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to delete stale HLS segment before re-encode: {Path}", segmentPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate stale HLS segments before re-encode: {Path}", hlsDirPath);
        }
#pragma warning restore CA3003
    }

    /// <summary>
    /// Checks total cache size and evicts oldest entries until under the limit.
    /// Handles both flat MP4 files (legacy) and HLS directories.
    /// Entries are evicted by last access time (oldest first).
    /// Since JF-537.1 this POST-ENCODE form also runs the transient-root idle
    /// reaper first: the production callers are the background monitor sweeps
    /// (<c>Task.Run</c> after an encode completes), so the reaper's recursive
    /// deletes of multi-GB idle entries never land on the Alexa request path
    /// (the pre-encode budget sweep <see cref="EnsureDiskBudgetBeforeEncodeAsync"/>
    /// deliberately does NOT reap: a transient encode reserves nothing in the
    /// capped cache, so there is no budget reason to reclaim transient bytes
    /// before it starts).
    /// </summary>
    /// <returns>A task representing the asynchronous eviction operation.</returns>
    public Task EvictIfNeeded()
    {
        ReapIdleTransientEntries();
        return EvictIfNeeded(0);
    }

    /// <summary>
    /// Pre-encode disk budget reservation (JF-310/JF-428): eviction sweep with the
    /// incoming encode's estimated size reserved as headroom; no refusal path by
    /// design (see <see cref="EvictIfNeededCore"/> for the floor and the pin contract).
    /// The estimate means the encode's IN-CACHE footprint: JF-537.1 transient
    /// encodes pass 0 (they write nothing into the capped cache, so reserving
    /// headroom there would evict real entries for nothing); any future change
    /// that floors or inflates headroom inside the sweep silently regresses that
    /// leg back to the real-cache churn the transient mode exists to remove.
    /// </summary>
    /// <param name="estimatedEncodeBytes">The estimated on-disk size the incoming encode writes into the capped cache.</param>
    public Task EnsureDiskBudgetBeforeEncodeAsync(long estimatedEncodeBytes)
        => EvictIfNeeded(estimatedEncodeBytes);

    /// <summary>
    /// JF-431: latency budget for the synchronous cache-directory scan in
    /// <see cref="EvictIfNeededCore"/>. A scan at or above this duration is logged at
    /// Information level: every cache the default cap could build measured under it
    /// on the deployment-class host when the threshold was set (JF-431, at the
    /// then-2048MB default; JF-534 raised the default to 4096, so roughly double the
    /// entries), so crossing it means a non-default configuration or storage slower
    /// than measured. The measurement table backing this threshold lives
    /// in the JF-431 task notes (single home, JF-448 review F8).
    /// </summary>
    private const double SlowEvictionScanThresholdMs = 50;

    private Task EvictIfNeeded(long headroomBytes)
    {
        EvictIfNeededCore(headroomBytes);
        return Task.CompletedTask;
    }

    private void EvictIfNeededCore(long headroomBytes)
    {
        int maxSizeMB = Plugin.Instance?.Configuration.VideoAudioCacheSizeMB ?? DefaultCacheCapMB;
        long capBytes = (long)maxSizeMB * 1024 * 1024;

        // JF-428 floor: a headroom that exceeds the whole cache (single audiobook
        // larger than the configured cap) must floor the eviction target at HALF the
        // cap, never zero. The old clamp-to-0 still TARGETED zero and the loop deleted
        // every entry trying to reach it, including the HLS directory of the audiobook
        // currently being encoded and streamed (undersized config = full wipe on every
        // encode start). Half the cap bounds what a pre-encode sweep can evict; the
        // post-encode sweep (headroom 0) still enforces the full cap.
        long maxSizeBytes = Math.Max(capBytes - headroomBytes, capBytes / 2);

        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        // JF-431 (kept SYNCHRONOUS by decision): this scan runs on the Alexa request
        // path (every gated encode start) and enumerates the whole cache directory.
        // Measured medians on the N100 deploy target and the dev hosts stay under the
        // SlowEvictionScanThresholdMs budget at every cache size the default cap
        // built when measured (2048MB; the JF-534 default raise doubles the entry
        // count), with margin even page-cache-cold; the full measurement table
        // lives in the task notes (JF-448 review F8 dedup: numbers in ONE place).
        // Decision constraint: a cached-size ledger or background sweep (the
        // alternatives) adds stale-total risk against no measurable win at these
        // sizes, and the JF-428 pin-before-sweep + half-cap-floor semantics depend on
        // this sweep running synchronously before every encode. The threshold log
        // below is the tripwire: if a deployment's scan exceeds it, the
        // synchronous-sweep decision no longer holds.
        var scanWatch = Stopwatch.StartNew();

        var entries = new List<CacheEntry>();

        try
        {
            var cacheDirInfo = new DirectoryInfo(_cacheDir);

            // LOAD-BEARING: both enumerations below are top-directory-only, the
            // file glob cannot reach a subtree, and the DIRECTORY loop below
            // skips the transient subtree BY NAME (not by glob shape): the
            // transient root's bytes must never count toward the cap (JF-537.1).
            // Collect flat MP4 files (legacy cache entries)
            foreach (var file in cacheDirInfo.GetFiles("*.mp4", SearchOption.TopDirectoryOnly))
            {
                DateTime mp4Access = _lastAccessUtc.TryGetValue(file.FullName, out var mp4Served) ? mp4Served : file.LastAccessTimeUtc;
                entries.Add(new CacheEntry(
                    file.FullName,
                    file.Length,
                    mp4Access,
                    isDirectory: false));
            }

            // Collect HLS directories (each directory is one cache entry)
            foreach (var dir in cacheDirInfo.GetDirectories("*_*", SearchOption.TopDirectoryOnly))
            {
                // Structural exclusion (not glob-emergent): even a broadened
                // pattern above must never sweep the transient subtree.
                if (dir.Name == TransientSubDirName)
                {
                    continue;
                }

                // Only count directories that contain an HLS playlist
                string playlistPath = Path.Combine(dir.FullName, "stream.m3u8");
                if (!File.Exists(playlistPath))
                {
                    continue;
                }

                long dirSize = 0;
                DateTime lastAccess = dir.LastAccessTimeUtc;

                try
                {
                    foreach (var f in dir.GetFiles("*", SearchOption.TopDirectoryOnly))
                    {
                        dirSize += f.Length;
                        if (f.LastAccessTimeUtc > lastAccess)
                        {
                            lastAccess = f.LastAccessTimeUtc;
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Error scanning HLS directory for eviction: {Path}", dir.FullName);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "Access denied scanning HLS directory for eviction: {Path}", dir.FullName);
                    continue;
                }

                if (dirSize > 0)
                {
                    DateTime dirAccess = _lastAccessUtc.TryGetValue(dir.FullName, out var dirServed) ? dirServed : lastAccess;
                    entries.Add(new CacheEntry(
                        dir.FullName,
                        dirSize,
                        dirAccess,
                        isDirectory: true));
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Error scanning cache directory for eviction");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied while scanning cache directory for eviction");
            return;
        }
        finally
        {
            // Runs on every exit from the enumeration, including the catch returns above:
            // an I/O-failure scan must still report its duration, because a scan that was
            // slow AND failed is exactly the case the JF-431 threshold exists to flag.
            LogEvictionScanCost(scanWatch, entries);
        }

        long totalSize = entries.Sum(e => e.Size);

        if (entries.Count == 0)
        {
            return;
        }

        if (totalSize <= maxSizeBytes)
        {
            return;
        }

        _logger.LogInformation(
            "VideoAudio cache over limit: {TotalMB:F1}MB / {LimitMB}MB — evicting oldest entries",
            totalSize / (1024.0 * 1024.0),
            maxSizeMB);

        // Sort by last access time ascending (oldest first)
        var sorted = entries.OrderBy(e => e.LastAccessTimeUtc).ToList();

        // One playback-pin cutoff for the whole sweep (JF-498 review C1), computed
        // before the loop so every entry is judged against the same instant.
        DateTime playbackExemptCutoffUtc = DateTime.UtcNow - PlaybackEvictionExemptionTtl;

        foreach (var entry in sorted)
        {
            if (totalSize <= maxSizeBytes)
            {
                break;
            }

            // JF-428: never evict an in-use (pinned) entry; its size still counts
            // toward totalSize. When pins (or failed deletes) make the target
            // unreachable, every UNPINNED entry is still evicted oldest-first before
            // the loop stops and warns below; that is the sweep doing its job with
            // the entries it is allowed to touch.
            if (_pinnedPaths.ContainsKey(entry.Path))
            {
                continue;
            }

            // JF-498 review C1 (playback pin): never evict an entry served inside the
            // TTL window either. The write pin above releases when ffmpeg exits, which
            // for a completed remux is minutes into a multi-hour watch; without this
            // check the post-encode sweep (targeting the full cap) then deleted the
            // just-encoded directory the client was still fetching segments from,
            // stalling playback with 404s and forcing a from-zero re-encode. An exempt
            // entry alone exceeding the cap is tolerated (see PlaybackEvictionExemptionTtl).
            if (_lastAccessUtc.TryGetValue(entry.Path, out DateTime lastServed)
                && lastServed >= playbackExemptCutoffUtc)
            {
                continue;
            }

            try
            {
                if (entry.IsDirectory)
                {
                    Directory.Delete(entry.Path, recursive: true);
                    _logger.LogDebug("Evicted HLS cache directory: {Path} ({SizeMB:F1}MB)", entry.Path, entry.Size / (1024.0 * 1024.0));
                }
                else
                {
                    File.Delete(entry.Path);
                    _logger.LogDebug("Evicted cache file: {Path} ({SizeMB:F1}MB)", entry.Path, entry.Size / (1024.0 * 1024.0));
                }

                totalSize -= entry.Size;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to evict cache entry: {Path}", entry.Path);
            }
            catch (UnauthorizedAccessException ex)
            {
                // An undeletable entry (e.g. a root-owned subdir left by a podman cp)
                // must not abort the sweep: the throw would escape onto the Alexa
                // request path and, while the cache stays over cap, fail every
                // subsequent encode. Skip it and keep evicting the other entries.
                _logger.LogWarning(ex, "Access denied deleting cache entry, skipping it (other entries still evict): {Path}", entry.Path);
            }

            // Drop the in-memory access record whether or not the delete succeeded -- if the
            // delete failed the file is re-enumerated next round (atime fallback), and this
            // avoids orphaning the entry if the file is later removed out-of-band.
            _lastAccessUtc.TryRemove(entry.Path, out _);
        }

        if (totalSize > maxSizeBytes)
        {
            _logger.LogWarning(
                "VideoAudio cache still {OverMB:F1}MB over the {TargetMB:F1}MB target after eviction (pinned in-use entries, recently-served entries, and/or failed deletes)",
                (totalSize - maxSizeBytes) / (1024.0 * 1024.0),
                maxSizeBytes / (1024.0 * 1024.0));
        }
    }

    /// <summary>
    /// JF-431 scan-cost visibility for the enumeration half of
    /// <see cref="EvictIfNeededCore"/> (the scan, not the deletes). Debug always;
    /// Information only past the measured-cheap threshold, so a config or storage
    /// regime that invalidates the keep-it-synchronous decision surfaces itself.
    /// Called from the enumeration's finally: a slow scan of a directory whose contents
    /// yield zero entries, or one that ends in an I/O failure, is exactly the
    /// pathological case the threshold exists to flag.
    /// </summary>
    /// <param name="scanWatch">Watch started immediately before the enumeration.</param>
    /// <param name="entries">Entries collected before the scan ended (partial on failure).</param>
    private void LogEvictionScanCost(Stopwatch scanWatch, List<CacheEntry> entries)
    {
        double scanMs = scanWatch.Elapsed.TotalMilliseconds;
        double totalMB = entries.Sum(e => e.Size) / (1024.0 * 1024.0);
        _logger.LogDebug(
            "VideoAudio cache eviction scan: {Entries} entries, {TotalMB:F1}MB, {ElapsedMs:F1}ms",
            entries.Count,
            totalMB,
            scanMs);
        if (scanMs >= SlowEvictionScanThresholdMs)
        {
            _logger.LogInformation(
                "VideoAudio cache eviction scan took {ElapsedMs:F1}ms for {Entries} entries ({TotalMB:F1}MB), above the {ThresholdMs:F0}ms measured-cheap budget (JF-431); consider lowering VideoAudioCacheSizeMB or moving the cache to faster storage",
                scanMs,
                entries.Count,
                totalMB,
                SlowEvictionScanThresholdMs);
        }
    }

    /// <summary>
    /// The JF-537.1 idle reaper: delete every TRANSIENT-root generation directory
    /// whose last liveness signal is older than <see cref="TransientIdleReapTtl"/>
    /// and that holds no <see cref="Pin"/>. Liveness signal: a recorded serve in
    /// <see cref="_lastAccessUtc"/> (every playlist and segment fetch refreshes it,
    /// so an entry being watched right now is never reaped), else the directory's
    /// own write time: every write ffmpeg makes here is a NEW directory entry
    /// (each segment is a new file; the playlist lands via tmp+rename), so the
    /// dir mtime IS the last write, a fresh encode's dir is live by construction,
    /// and an untouched pre-restart entry is judged by when its encode last wrote
    /// it. The pin covers the in-flight encode window exactly as it does for the
    /// cap sweep. Best-effort like the cap sweep: an undeletable or unreadable
    /// directory is skipped, never thrown. Trigger: the POST-ENCODE eviction
    /// sweep (<see cref="EvictIfNeeded()"/>, run by the background monitors via
    /// Task.Run) deliberately, with no background timer, because a timer-owned
    /// reaper would leak per test-constructed cache instance and need
    /// host-lifetime disposal this cache singleton does not have, and running it
    /// on the pre-encode budget sweep would put recursive multi-GB deletes inline
    /// on the Alexa play path for no budget reason (a transient encode reserves
    /// nothing in the capped cache). The honest cost is that an idle entry
    /// lingers until the next encode completes anywhere in the plugin.
    /// The scan below carries its own cost tripwire at the same
    /// <see cref="SlowEvictionScanThresholdMs"/> budget as the cap sweep's
    /// enumeration (JF-431): this root is deliberately unbounded (no byte
    /// budget), so its growth must stay visible.
    /// </summary>
    internal void ReapIdleTransientEntries()
    {
        if (!Directory.Exists(_transientDir))
        {
            return;
        }

        DateTime reapCutoffUtc = DateTime.UtcNow - TransientIdleReapTtl;
        var scanWatch = Stopwatch.StartNew();

        DirectoryInfo[] generationDirs = Array.Empty<DirectoryInfo>();
        int reaped = 0;
        try
        {
            generationDirs = new DirectoryInfo(_transientDir)
                .GetDirectories("*_*", SearchOption.TopDirectoryOnly);

            foreach (DirectoryInfo dir in generationDirs)
            {
                // JF-428: never reap an in-use (pinned) directory; an encode writing
                // here right now holds the pin from process start to exit (+500ms poll).
                if (_pinnedPaths.ContainsKey(dir.FullName))
                {
                    continue;
                }

                // Recorded serve (playlist/segment fetch) beats the write time; a
                // serve inside the TTL means the entry is being watched.
                DateTime lastLivenessUtc = dir.LastWriteTimeUtc;
                if (_lastAccessUtc.TryGetValue(dir.FullName, out DateTime lastServed) && lastServed > lastLivenessUtc)
                {
                    lastLivenessUtc = lastServed;
                }

                if (lastLivenessUtc >= reapCutoffUtc)
                {
                    continue;
                }

                // The delete itself is the dir-scoped cleanup helper's contract
                // (best-effort recursive delete + access-record drop); the reaper
                // only adds the announcement, and only when the delete actually
                // landed (the helper swallows its failures at Debug).
                CleanupHlsGenerationAt(dir.FullName);
                if (!Directory.Exists(dir.FullName))
                {
                    _logger.LogInformation(
                        "VideoAudio transient HLS reaped (idle beyond {TtlMinutes:F0}min): {Path}",
                        TransientIdleReapTtl.TotalMinutes,
                        dir.FullName);
                    reaped++;
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Vanished mid-scan or read-denied: reap what already happened, then
            // stop; the next sweep retries.
            _logger.LogDebug(ex, "Error scanning the transient root for idle reaping: {Path}", _transientDir);
        }

        double scanMs = scanWatch.Elapsed.TotalMilliseconds;
        _logger.LogDebug(
            "VideoAudio transient root reap scan: {Entries} entries, {Reaped} reaped, {ElapsedMs:F1}ms",
            generationDirs.Length,
            reaped,
            scanMs);
        if (scanMs >= SlowEvictionScanThresholdMs)
        {
            _logger.LogInformation(
                "VideoAudio transient root reap scan took {ElapsedMs:F1}ms for {Entries} entries, above the {ThresholdMs:F0}ms measured-cheap budget (JF-431); idle oversize encodes are accumulating under {Path} (raise VideoAudioCacheSizeMB so fewer encodes qualify transient, or clear that directory)",
                scanMs,
                generationDirs.Length,
                SlowEvictionScanThresholdMs,
                _transientDir);
        }
    }

    /// <summary>
    /// Represents a cache entry for eviction — either a flat file or a directory.
    /// </summary>
    private sealed class CacheEntry
    {
        public string Path { get; }
        public long Size { get; }
        public DateTime LastAccessTimeUtc { get; }
        public bool IsDirectory { get; }

        public CacheEntry(string path, long size, DateTime lastAccessTimeUtc, bool isDirectory)
        {
            Path = path;
            Size = size;
            LastAccessTimeUtc = lastAccessTimeUtc;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// Removes all cached files and HLS directories for a given item ID regardless of art modification ticks.
    /// MANUAL-INVALIDATION UTILITY ONLY (JF-676): since the debris verdicts went
    /// ticks-scoped this method has NO production caller; never call it from a
    /// serve/verdict path, where a sibling art-tick generation of the same key
    /// may be live-writing a directory this wipe would delete mid-write; use
    /// <see cref="CleanupHlsGenerationAt"/> there (one generation's directory,
    /// never a sibling's).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to invalidate.</param>
    public void Cleanup(string itemId)
    {
#pragma warning disable CA3003 // itemId is GUID-validated by callers before reaching this method
        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        string prefix = itemId + "_";
        int deleted = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDir, $"{prefix}*.mp4"))
            {
                try
                {
                    System.IO.File.Delete(file);
                    _lastAccessUtc.TryRemove(file, out _);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to delete cache file: {Path}", file);
                }
            }

            // Also clean up HLS directories for this item
            foreach (var dir in Directory.EnumerateDirectories(_cacheDir, $"{prefix}*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _lastAccessUtc.TryRemove(dir, out _);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to delete HLS cache directory: {Path}", dir);
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // Cache root already gone, or read-denied so the enumeration itself
            // fails (JF-499 W4): nothing can be cleaned up either way.
        }

        if (deleted > 0)
        {
            _logger.LogDebug("Cleaned up {Count} cache file(s)/dir(s) for item {ItemId}", deleted, itemId);
        }
#pragma warning restore CA3003
    }

    /// <summary>
    /// Removes EXACTLY ONE generation directory (JF-676 ticks-scoping, JF-537.1
    /// dir-scoping): the ticks-scoped debris verdicts of <c>VideoAudioController</c>
    /// pass the directory the playlist they validated CAME FROM
    /// (<c>Path.GetDirectoryName(cached.FullName)</c>), so a verdict on one
    /// art-tick generation's stale playlist can never delete a sibling
    /// generation's directory (including a live foreign-ticks encode's, which the
    /// key-wide <see cref="Cleanup"/> would wipe mid-write), and since JF-537.1 it
    /// cannot destroy a same-key generation in the OTHER root either (the cap
    /// changed between plays): a key holding directories in both roots loses
    /// exactly the one that was served. ORPHAN COLLECTOR (the JF-676 rework's
    /// honest residual): a debris directory nobody requests again has no
    /// deterministic cleaner (the verdict deletes only the served-from
    /// directory, <see cref="CleanupHlsStub"/> skips non-empty playlists, and the
    /// per-file debris sweep runs only on the encode path for the caller's own
    /// directory); the size-cap LRU eviction sweep
    /// (<see cref="EvictIfNeeded()"/>) is the eventual collector for the cache
    /// root, and the idle reaper (<see cref="ReapIdleTransientEntries"/>) for the
    /// transient root. That is the deliberate trade for never deleting a live
    /// generation's directory. Deliberately does NOT touch the generation's flat
    /// <c>{itemId}_{ticks}.mp4</c> files: they are a different endpoint's
    /// artifact and not the verdict's subject. Best-effort, same failure family
    /// as <see cref="Cleanup"/> (logged, swallowed).
    /// </summary>
    /// <param name="dirPath">The one generation directory to remove.</param>
    public void CleanupHlsGenerationAt(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, recursive: true);
                _lastAccessUtc.TryRemove(dirPath, out _);
                _logger.LogDebug("Cleaned up HLS generation directory: {Path}", dirPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Lost a race with a concurrent delete of the same generation, or the
            // directory is undeletable: the verdict's caller re-encodes either way.
            _logger.LogDebug(ex, "Failed to delete HLS cache directory: {Path}", dirPath);
        }
    }

    /// <summary>
    /// Returns the HLS directory path for the given item and art modification ticks.
    /// Format: {cacheDir}/{itemId}_{artModifiedTicks}/
    /// Each HLS item gets its own subdirectory containing the playlist and segment files.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Full path to the HLS directory.</returns>
    public string GetHlsDirectoryPath(string itemId, long artModifiedTicks)
    {
        return Path.Combine(_cacheDir, $"{itemId}_{artModifiedTicks}");
    }

    /// <summary>
    /// Returns the HLS playlist file path for the given item and art modification ticks.
    /// Format: {cacheDir}/{itemId}_{artModifiedTicks}/stream.m3u8
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Full path to the HLS playlist file.</returns>
    public string GetHlsPlaylistPath(string itemId, long artModifiedTicks)
    {
        return Path.Combine(GetHlsDirectoryPath(itemId, artModifiedTicks), "stream.m3u8");
    }

    /// <summary>
    /// Returns the TRANSIENT-root HLS directory path for the given key and art
    /// ticks (JF-537.1): the target of oversize transcode encodes, whose bytes
    /// never count against the cap and are never swept by it. Same
    /// <c>{itemId}_{artModifiedTicks}</c> generation-name shape as the cache root,
    /// so every root-agnostic resolver just probes both roots.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID (or variant cache key).</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Full path to the transient-root HLS directory.</returns>
    public string GetTransientHlsDirectoryPath(string itemId, long artModifiedTicks)
    {
        return Path.Combine(_transientDir, $"{itemId}_{artModifiedTicks}");
    }

    /// <summary>
    /// The ordered generation-directory candidates for a key, cache root FIRST,
    /// transient root second (JF-537.1): the ONE home of the load-bearing
    /// root-preference order (a key with live generations in both roots resolves
    /// to the cache one, the preferred permanent home). Every root-agnostic
    /// resolver that walks per-key directories consumes this order
    /// (<see cref="GetCachedHlsPlaylist"/>, the controller's prewrite probe,
    /// <see cref="CleanupHlsStub"/>); the scan fallback
    /// (<see cref="FindHlsDirectoryByScan"/>) covers the same two roots at the
    /// generation level but orders by generation RECENCY across them (the cache
    /// root winning ties; JF-774), because a scan has no caller ticks to prefer
    /// and a stale orphan must not outrank the current generation.
    /// </summary>
    /// <param name="itemId">The cache key (Jellyfin item or variant key).</param>
    /// <param name="artModifiedTicks">Ticks of the generation.</param>
    /// <returns>The candidate directories in preference order.</returns>
    internal string[] HlsGenerationDirPaths(string itemId, long artModifiedTicks)
        => new[]
        {
            GetHlsDirectoryPath(itemId, artModifiedTicks),
            GetTransientHlsDirectoryPath(itemId, artModifiedTicks)
        };

    /// <summary>
    /// Returns the cached HLS playlist file if it exists and has any content, null otherwise.
    /// HLS playlists are served even when small because ffmpeg writes them atomically
    /// (.tmp rename): a non-empty file is always a valid partial or complete playlist.
    /// This allows the Echo Show to start playback as soon as the first segment is ready,
    /// without waiting for the entire content to be encoded.
    /// Root-agnostic since JF-537.1: the generation directories are probed in
    /// <see cref="HlsGenerationDirPaths"/> order (cache root first, then the
    /// transient root, an oversize encode's target). One probe pair covers the
    /// episode fast path, the in-lock double-check, and the concurrent-encode
    /// dedup at once; keys that never go transient (variants, audiobook, album)
    /// simply miss the transient probe.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Cached playlist file info or null if not cached.</returns>
    public Task<FileInfo?> GetCachedHlsPlaylist(string itemId, long artModifiedTicks)
    {
        string[] generationDirs = HlsGenerationDirPaths(itemId, artModifiedTicks);
        foreach (string dirPath in generationDirs)
        {
            FileInfo? hit = ProbeHlsPlaylist(dirPath);
            if (hit != null)
            {
                return Task.FromResult<FileInfo?>(hit);
            }
        }

        _logger.LogDebug(
            "VideoAudio HLS cache miss (probed {Roots} roots, cache root {CacheDir} first)",
            generationDirs.Length,
            generationDirs[0]);
        return Task.FromResult<FileInfo?>(null);
    }

    /// <summary>
    /// One root-probe of <see cref="GetCachedHlsPlaylist"/>: a non-empty
    /// <c>stream.m3u8</c> inside <paramref name="dirPath"/> records a serve of
    /// that directory (the playback-recency source) and returns its FileInfo,
    /// else null.
    /// </summary>
    /// <param name="dirPath">One root's generation directory.</param>
    /// <returns>The playlist FileInfo on a hit, null on a miss.</returns>
    private FileInfo? ProbeHlsPlaylist(string dirPath)
    {
        string playlistPath = Path.Combine(dirPath, "stream.m3u8");
#pragma warning disable CA3003 // paths are GUID-derived by the callers
        var fi = new FileInfo(playlistPath);
#pragma warning restore CA3003

        if (fi.Exists && fi.Length > 0)
        {
            _logger.LogDebug("VideoAudio HLS cache hit: {Path} ({Size} bytes)", playlistPath, fi.Length);
            RecordAccess(dirPath);
            return fi;
        }

        return null;
    }

    /// <summary>
    /// Probe ONLY the transient root's generation directory for a non-empty
    /// <c>stream.m3u8</c> (JF-774 finding 3): the controller's fall-through when
    /// a cache-root playlist hit fails its debris verdict, so an undeletable
    /// cache-root playlist cannot mask the same-key VALID transient entry into a
    /// from-zero re-encode. Records a serve of the directory on a hit, exactly
    /// like <see cref="GetCachedHlsPlaylist"/>'s per-root probe.
    /// </summary>
    /// <param name="itemId">The cache key (Jellyfin item or variant key).</param>
    /// <param name="artModifiedTicks">Ticks of the generation.</param>
    /// <returns>The transient playlist FileInfo on a hit, null on a miss.</returns>
    internal FileInfo? ProbeTransientHlsPlaylist(string itemId, long artModifiedTicks)
        => ProbeHlsPlaylist(GetTransientHlsDirectoryPath(itemId, artModifiedTicks));

    /// <summary>
    /// The in-memory registration half of <see cref="FindHlsDirectory"/> WITHOUT
    /// the scan fallback: the directory a live or recently finished encode
    /// registered for the item, or null when no registration exists (e.g. after
    /// a restart). JF-774 finding 2: the episode prewrite probe prefers this
    /// directory while its encode is live, so a stale prewrite that survived in
    /// the OTHER root cannot shadow the live encode's fresh listing.
    /// </summary>
    /// <param name="itemId">The cache key (Jellyfin item or variant key).</param>
    /// <returns>The registered directory when it still exists, else null.</returns>
    internal string? TryGetRegisteredHlsDirectory(string itemId)
    {
#pragma warning disable CA3003 // registered paths come from GUID-derived cache keys
        return _hlsDirLookup.TryGetValue(itemId, out string? registered) && Directory.Exists(registered)
            ? registered
            : null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// Validates that a segment name matches the expected pattern (seg_NNN.ts).
    /// Uses simple string checks instead of regex for zero-allocation hot-path performance.
    /// Called on every segment request (~45-225 times per playback).
    /// </summary>
    /// <param name="segmentName">The segment file name to validate.</param>
    /// <returns>True if the name is valid, false otherwise.</returns>
    public static bool IsValidSegmentName(string segmentName)
    {
        // Expected formats:
        //   seg_NNN.ts   (3 digits, 10 chars) — single-item HLS
        //   seg_NNNN.ts  (4 digits, 11 chars) — audiobook concat HLS
        // The prefix and suffix are checked first, then all chars between must be digits.
        if (!segmentName.StartsWith("seg_", StringComparison.Ordinal)
            || !segmentName.EndsWith(".ts", StringComparison.Ordinal))
        {
            return false;
        }

        // "seg_" = 4 chars, ".ts" = 3 chars → middle part is the digit sequence
        int digitCount = segmentName.Length - 7; // 4 + 3
        if (digitCount < 3 || digitCount > 4)
        {
            return false;
        }

        for (int i = 4; i < 4 + digitCount; i++)
        {
            if (!char.IsDigit(segmentName[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the segment file path for a given item and segment name.
    /// Uses the in-memory directory lookup (O(1)) populated by <see cref="RegisterHlsDirectoryPath"/>
    /// (directly or via the ticks-form <see cref="RegisterHlsDirectory"/>).
    /// Falls back to filesystem scan if the in-memory cache misses (e.g. after restart).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_000.ts").</param>
    /// <returns>Full path to the segment file, or null if not found.</returns>
    public string? FindSegmentPath(string itemId, string segmentName)
    {
        if (!IsValidSegmentName(segmentName))
        {
            return null;
        }

        // Directory resolution (in-memory O(1) lookup, filesystem scan fallback) is
        // owned by FindHlsDirectory.
        string? hlsDir = FindHlsDirectory(itemId);
        if (hlsDir == null)
        {
            return null;
        }

#pragma warning disable CA3003 // hlsDir resolved from GUID-validated itemId paths
        string path = Path.Combine(hlsDir, segmentName);
        if (File.Exists(path))
        {
            RecordAccess(hlsDir);
            return path;
        }

        return null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// Finds the HLS directory for an item by scanning the cache directory for subdirectories
    /// matching the pattern {itemId}_*. Returns the most recently created one, or null if
    /// no matching directory exists. Used as a fallback when the in-memory lookup misses.
    /// Root-agnostic since JF-537.1: BOTH roots are scanned and the NEWEST generation
    /// across them wins (JF-774 finding 1: the former cache-root-first preference
    /// outranked generation recency across roots, so an old orphaned cache-root
    /// generation, the documented JF-676 orphan, shadowed the CURRENT transient
    /// generation's segments after a restart, serving wrong content that then
    /// pinned for the process lifetime); each root picks its own most-recent
    /// generation, and the cache root still wins a tie because it is the
    /// preferred permanent home.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>Full path to the HLS directory, or null if not found.</returns>
    internal string? FindHlsDirectoryByScan(string itemId)
    {
        DirectoryInfo? cacheHit = ScanRootForNewestGeneration(_cacheDir, itemId);
        DirectoryInfo? transientHit = ScanRootForNewestGeneration(_transientDir, itemId);

        if (cacheHit == null || transientHit == null)
        {
            return (cacheHit ?? transientHit)?.FullName;
        }

        return cacheHit.CreationTimeUtc >= transientHit.CreationTimeUtc
            ? cacheHit.FullName
            : transientHit.FullName;
    }

    /// <summary>
    /// One root-scan of <see cref="FindHlsDirectoryByScan"/>: the most recently
    /// created <c>{itemId}_*</c> generation directory under <paramref name="root"/>,
    /// or null when the root or the generation is absent or unreadable. Returns the
    /// <see cref="DirectoryInfo"/> (not just the path) so the cross-root recency
    /// comparison in <see cref="FindHlsDirectoryByScan"/> reads CreationTimeUtc
    /// without a second stat.
    /// </summary>
    /// <param name="root">The root directory to scan (cache or transient).</param>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The newest matching directory, or null.</returns>
    private DirectoryInfo? ScanRootForNewestGeneration(string root, string itemId)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        string prefix = itemId + "_";

        try
        {
            var dirs = new DirectoryInfo(root)
                .GetDirectories($"{prefix}*", SearchOption.TopDirectoryOnly);

            if (dirs.Length == 0)
            {
                return null;
            }

            return dirs.OrderByDescending(d => d.CreationTimeUtc).First();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // IOException: transient scan failure. UnauthorizedAccessException: a
            // read-denied cache root must degrade to a cache miss (JF-499 W4), not
            // 500 a serve path.
            _logger.LogDebug(ex, "Error scanning cache directory for HLS directory lookup");
            return null;
        }
    }

    /// <summary>
    /// Resolve the HLS cache directory for an item: the in-memory O(1) lookup first
    /// (populated by <see cref="RegisterHlsDirectoryPath"/>, directly or via the
    /// ticks-form <see cref="RegisterHlsDirectory"/>), falling back to the
    /// filesystem scan (e.g. after a restart). Same resolution order as
    /// <see cref="FindSegmentPath"/>. Returns null when no directory exists.
    /// JF-503: used by the controller's hold-for-segment path to compute the running
    /// encode's head (the highest existing segment number in the directory).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The HLS directory path, or null when none exists.</returns>
    internal string? FindHlsDirectory(string itemId)
    {
        // The in-memory half is <see cref="TryGetRegisteredHlsDirectory"/>'s ONE
        // predicate (JF-774): the lookup+exists condition must not drift from the
        // registration read the prewrite probe consumes.
        string? registered = TryGetRegisteredHlsDirectory(itemId);
        if (registered != null)
        {
            return registered;
        }

        string? hlsDir = FindHlsDirectoryByScan(itemId);
        if (hlsDir != null)
        {
            _hlsDirLookup.TryAdd(itemId, hlsDir);
        }

        return hlsDir;
    }

    /// <summary>
    /// Release the per-item lock and clean up the SemaphoreSlim if no one else is using it.
    /// Called by <see cref="Releaser.Dispose"/>.
    /// </summary>
    private void ReleaseLock(string key)
    {
        if (_itemLocks.TryGetValue(key, out var rc))
        {
            rc.Semaphore.Release();
            if (Interlocked.Decrement(ref rc.RefCount) == 0)
            {
                // No one waiting or holding — remove from dictionary and dispose
                if (_itemLocks.TryRemove(key, out var removed))
                {
                    removed.Semaphore.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Reference-counted lock wrapper. Tracks how many holders/waiters exist for a given key.
    /// When RefCount drops to 0, the entry is removed from the dictionary and the SemaphoreSlim
    /// is disposed. The SemaphoreSlim is owned by this class and is only disposed when RefCount
    /// reaches 0, ensuring no one is waiting on or holding the semaphore at that point.
    /// </summary>
#pragma warning disable CA1001 // SemaphoreSlim is disposed via TryRemove path in ReleaseLock
    private sealed class RefCountedLock
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }
#pragma warning restore CA1001

    /// <summary>
    /// Async disposable that releases the per-item lock on dispose.
    /// Used with <c>await using</c> to ensure lock release even on exceptions.
    /// </summary>
    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly VideoAudioCache _owner;
        private int _disposed;

        public Releaser(string key, VideoAudioCache owner)
        {
            _key = key;
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseLock(_key);
            }
        }
    }
}
