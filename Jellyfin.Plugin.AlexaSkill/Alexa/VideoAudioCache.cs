using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private readonly ILogger<VideoAudioCache> _logger;
    private readonly string _cacheDir;

    /// <summary>
    /// Per-item locks keyed by cache file path. Prevents concurrent ffmpeg
    /// processes for the same item while allowing different items to generate
    /// in parallel. Uses reference counting to clean up SemaphoreSlim objects
    /// when no longer needed (same pattern as Jellyfin's TranscodeManager).
    /// </summary>
    private readonly ConcurrentDictionary<string, RefCountedLock> _itemLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoAudioCache"/> class.
    /// </summary>
    /// <param name="appPaths">Jellyfin application paths for locating the cache directory.</param>
    /// <param name="logger">Logger instance.</param>
    public VideoAudioCache(IApplicationPaths appPaths, ILogger<VideoAudioCache> logger)
    {
        _logger = logger;
        _cacheDir = Path.Combine(appPaths.CachePath, CacheSubDir);
    }

    /// <summary>
    /// Gets the cache directory path (exposed for testing).
    /// </summary>
    internal string CacheDir => _cacheDir;

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
            catch (IOException ex)
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
        var rc = _itemLocks.GetOrAdd(key, _ => new RefCountedLock());
        Interlocked.Increment(ref rc.RefCount);
        await rc.Semaphore.WaitAsync().ConfigureAwait(false);
        return new Releaser(key, this);
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
    /// Register the HLS directory path for an item so segment lookups are O(1).
    /// Called after ffmpeg finishes generating the HLS playlist and segments.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    public void RegisterHlsDirectory(string itemId, long artModifiedTicks)
    {
        _hlsDirLookup[itemId] = GetHlsDirectoryPath(itemId, artModifiedTicks);
    }

    /// <summary>
    /// Clean up a corrupt/partial HLS directory from a previous failed generation.
    /// Only called inside the per-item lock to avoid racing with active generation.
    /// Deletes the directory only if the playlist file is missing or empty (0 bytes),
    /// which indicates ffmpeg never successfully wrote a segment.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    public void CleanupHlsStub(string itemId, long artModifiedTicks)
    {
        string dirPath = GetHlsDirectoryPath(itemId, artModifiedTicks);
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
            catch (IOException ex) { _logger.LogDebug(ex, "Failed to delete stale HLS directory: {Path}", dirPath); }
            return;
        }

        var fi = new FileInfo(playlistPath);
        if (fi.Length == 0)
        {
            _logger.LogWarning("VideoAudio HLS: removing stub directory (empty playlist): {Path}", dirPath);
            try { Directory.Delete(dirPath, recursive: true); }
            catch (IOException ex) { _logger.LogDebug(ex, "Failed to delete HLS stub directory: {Path}", dirPath); }
        }
#pragma warning restore CA3003
    }

    /// <summary>
    /// Checks total cache size and evicts oldest entries until under the limit.
    /// Handles both flat MP4 files (legacy) and HLS directories.
    /// Entries are evicted by last access time (oldest first).
    /// </summary>
    /// <returns>A task representing the asynchronous eviction operation.</returns>
    public Task EvictIfNeeded()
    {
        int maxSizeMB = Plugin.Instance?.Configuration.VideoAudioCacheSizeMB ?? 2048;
        long maxSizeBytes = (long)maxSizeMB * 1024 * 1024;

        if (!Directory.Exists(_cacheDir))
        {
            return Task.CompletedTask;
        }

        var entries = new List<CacheEntry>();

        try
        {
            var cacheDirInfo = new DirectoryInfo(_cacheDir);

            // Collect flat MP4 files (legacy cache entries)
            foreach (var file in cacheDirInfo.GetFiles("*.mp4", SearchOption.TopDirectoryOnly))
            {
                entries.Add(new CacheEntry(
                    file.FullName,
                    file.Length,
                    file.LastAccessTimeUtc,
                    isDirectory: false));
            }

            // Collect HLS directories (each directory is one cache entry)
            foreach (var dir in cacheDirInfo.GetDirectories("*_*", SearchOption.TopDirectoryOnly))
            {
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

                if (dirSize > 0)
                {
                    entries.Add(new CacheEntry(
                        dir.FullName,
                        dirSize,
                        lastAccess,
                        isDirectory: true));
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Task.CompletedTask;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Error scanning cache directory for eviction");
            return Task.CompletedTask;
        }

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        long totalSize = entries.Sum(e => e.Size);

        if (totalSize <= maxSizeBytes)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "VideoAudio cache over limit: {TotalMB:F1}MB / {LimitMB}MB — evicting oldest entries",
            totalSize / (1024.0 * 1024.0),
            maxSizeMB);

        // Sort by last access time ascending (oldest first)
        var sorted = entries.OrderBy(e => e.LastAccessTimeUtc).ToList();

        foreach (var entry in sorted)
        {
            if (totalSize <= maxSizeBytes)
            {
                break;
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
        }

        return Task.CompletedTask;
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
    /// Used for manual invalidation when needed.
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
                    deleted++;
                }
                catch (IOException ex)
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
                    deleted++;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Failed to delete HLS cache directory: {Path}", dir);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone — nothing to clean up
        }

        if (deleted > 0)
        {
            _logger.LogDebug("Cleaned up {Count} cache file(s)/dir(s) for item {ItemId}", deleted, itemId);
        }
#pragma warning restore CA3003
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
    /// Returns the cached HLS playlist file if it exists and has any content, null otherwise.
    /// HLS playlists are served even when small because ffmpeg writes them atomically
    /// (.tmp rename) — a non-empty file is always a valid partial or complete playlist.
    /// This allows the Echo Show to start playback as soon as the first segment is ready,
    /// without waiting for the entire content to be encoded.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified.</param>
    /// <returns>Cached playlist file info or null if not cached.</returns>
    public Task<FileInfo?> GetCachedHlsPlaylist(string itemId, long artModifiedTicks)
    {
        string path = GetHlsPlaylistPath(itemId, artModifiedTicks);
#pragma warning disable CA3003
        var fi = new FileInfo(path);
#pragma warning restore CA3003

        if (fi.Exists && fi.Length > 0)
        {
            _logger.LogDebug("VideoAudio HLS cache hit: {Path} ({Size} bytes)", path, fi.Length);
            return Task.FromResult<FileInfo?>(fi);
        }

        _logger.LogDebug("VideoAudio HLS cache miss: {Path}", path);
        return Task.FromResult<FileInfo?>(null);
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
    /// Uses the in-memory directory lookup (O(1)) populated by <see cref="RegisterHlsDirectory"/>.
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

        // Fast path: in-memory lookup (O(1), no filesystem access)
#pragma warning disable CA3003 // cachedDir is populated from GUID-validated itemId paths
        if (_hlsDirLookup.TryGetValue(itemId, out string? cachedDir)
            && Directory.Exists(cachedDir))
        {
            string segmentPath = Path.Combine(cachedDir, segmentName);
#pragma warning disable CA3003
            return File.Exists(segmentPath) ? segmentPath : null;
#pragma warning restore CA3003 // cachedDir from GUID-validated itemId paths
        }

        // Slow path: filesystem scan (fallback after restart or cache miss)
        string? hlsDir = FindHlsDirectoryByScan(itemId);
        if (hlsDir == null)
        {
            return null;
        }

        // Populate the in-memory cache for future requests
        _hlsDirLookup.TryAdd(itemId, hlsDir);

        string path = Path.Combine(hlsDir, segmentName);
#pragma warning disable CA3003
        return File.Exists(path) ? path : null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// Finds the HLS directory for an item by scanning the cache directory for subdirectories
    /// matching the pattern {itemId}_*. Returns the most recently created one, or null if
    /// no matching directory exists. Used as a fallback when the in-memory lookup misses.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>Full path to the HLS directory, or null if not found.</returns>
    internal string? FindHlsDirectoryByScan(string itemId)
    {
        if (!Directory.Exists(_cacheDir))
        {
            return null;
        }

        string prefix = itemId + "_";

        try
        {
            var dirs = new DirectoryInfo(_cacheDir)
                .GetDirectories($"{prefix}*", SearchOption.TopDirectoryOnly);

            if (dirs.Length == 0)
            {
                return null;
            }

            return dirs.OrderByDescending(d => d.CreationTimeUtc).First().FullName;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Error scanning cache directory for HLS directory lookup");
            return null;
        }
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
