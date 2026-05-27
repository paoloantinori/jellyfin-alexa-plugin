using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// File-based cache for generated MP4 video-audio files.
/// Caches ffmpeg-generated MP4s so subsequent plays of the same item
/// (with same album art) are served instantly without re-encoding.
/// </summary>
public class VideoAudioCache
{
    private const string CacheSubDir = "alexaskill-video-audio";

    private readonly ILogger<VideoAudioCache> _logger;
    private readonly string _cacheDir;

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
    /// Returns the cached MP4 file if it exists and has non-zero size, null otherwise.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="artModifiedTicks">Ticks from the album art's DateModified for cache key invalidation.</param>
    /// <returns>Cached file info or null if not cached.</returns>
    public Task<FileInfo?> GetCachedFile(string itemId, long artModifiedTicks)
    {
        string path = GetCacheFilePath(itemId, artModifiedTicks);
#pragma warning disable CA3003 // itemId is validated as GUID by the caller (VideoAudioController)
        var fi = new FileInfo(path);
#pragma warning restore CA3003

        if (fi.Exists && fi.Length > 0)
        {
            _logger.LogDebug("VideoAudio cache hit: {Path} ({Size} bytes)", path, fi.Length);
            return Task.FromResult<FileInfo?>(fi);
        }

        _logger.LogDebug("VideoAudio cache miss: {Path}", path);
        return Task.FromResult<FileInfo?>(null);
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
}
