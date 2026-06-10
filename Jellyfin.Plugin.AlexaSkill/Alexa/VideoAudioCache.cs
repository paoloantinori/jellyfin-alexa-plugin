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
    /// Checks total cache size and evicts oldest files (by last access time) until under the limit.
    /// Called in the background after writing a new cache entry.
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

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(_cacheDir)
                .GetFiles("*.mp4", SearchOption.TopDirectoryOnly);
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

        if (files.Length == 0)
        {
            return Task.CompletedTask;
        }

        long totalSize = files.Sum(f => f.Length);

        if (totalSize <= maxSizeBytes)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "VideoAudio cache over limit: {TotalMB:F1}MB / {LimitMB}MB — evicting oldest files",
            totalSize / (1024.0 * 1024.0),
            maxSizeMB);

        // Sort by last access time ascending (oldest first)
        var sorted = files.OrderBy(f => f.LastAccessTimeUtc).ToList();

        foreach (var file in sorted)
        {
            if (totalSize <= maxSizeBytes)
            {
                break;
            }

            try
            {
                long fileSize = file.Length;
                file.Delete();
                totalSize -= fileSize;
                _logger.LogDebug("Evicted cache file: {Path} ({SizeMB:F1}MB)", file.Name, fileSize / (1024.0 * 1024.0));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to evict cache file: {Path}", file.FullName);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes all cached files for a given item ID regardless of art modification ticks.
    /// Used for manual invalidation when needed.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to invalidate.</param>
    public void Cleanup(string itemId)
    {
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
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone — nothing to clean up
        }

        if (deleted > 0)
        {
            _logger.LogDebug("Cleaned up {Count} cache file(s) for item {ItemId}", deleted, itemId);
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
