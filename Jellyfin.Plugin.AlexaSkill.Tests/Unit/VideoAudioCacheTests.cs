using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for the VideoAudioCache — file-based MP4 cache service.
/// </summary>
[Collection("Plugin")]
public class VideoAudioCacheTests : PluginTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly VideoAudioCache _cache;
    private readonly ILogger<VideoAudioCache> _logger;

    public VideoAudioCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "va-cache-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);

        _logger = LoggerFactory.Create(b => { }).CreateLogger<VideoAudioCache>();
        _cache = new VideoAudioCache(appPaths.Object, _logger);

        // Ensure plugin instance exists for config access in EvictIfNeeded
        EnsurePluginInstance(
            new PluginConfiguration(),
            LoggerFactory.Create(b => { }),
            cfg => { },
            "va-cache-test");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Temp dir cleanup best-effort
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetCacheFilePath_ContainsItemIdAndTicks()
    {
        string path = _cache.GetCacheFilePath("abc123", 637500000000000000);

        Assert.Contains("abc123", Path.GetFileName(path));
        Assert.Contains("637500000000000000", Path.GetFileName(path));
        Assert.EndsWith(".mp4", path);
        Assert.Contains("alexaskill-video-audio", path);
    }

    [Fact]
    public void GetCacheFilePath_DifferentTicks_DifferentPaths()
    {
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public async Task GetCachedFile_NoFile_ReturnsNull()
    {
        FileInfo? result = await _cache.GetCachedFile("nonexistent", 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedFile_FileExists_ReturnsFileInfo()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "fake mp4 content");

        FileInfo? result = await _cache.GetCachedFile("item1", 12345);

        Assert.NotNull(result);
        Assert.Equal(path, result!.FullName);
        Assert.True(result.Length > 0);
    }

    [Fact]
    public async Task GetCachedFile_EmptyFile_ReturnsNull()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Empty);

        FileInfo? result = await _cache.GetCachedFile("item1", 12345);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedFile_WrongTicks_ReturnsNull()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "content");

        FileInfo? result = await _cache.GetCachedFile("item1", 99999);

        Assert.Null(result);
    }

    [Fact]
    public async Task EvictIfNeeded_UnderLimit_DoesNothing()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 2048;

        // Create a small file
        string path = _cache.GetCacheFilePath("item1", 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "small");

        await _cache.EvictIfNeeded();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task EvictIfNeeded_OverLimit_DeletesOldestFiles()
    {
        // Set limit to 1 MB so only one small file survives
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        // Create two files with different access times
        // Make the first file large enough that deleting it brings us under limit
        string path1 = _cache.GetCacheFilePath("old-item", 1);
        string path2 = _cache.GetCacheFilePath("new-item", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);

        // Write 1.5 MB for old file (will be evicted)
        await File.WriteAllTextAsync(path1, new string('x', 1536 * 1024));
        File.SetLastAccessTimeUtc(path1, DateTime.UtcNow.AddHours(-2));

        // Write 100 KB for new file (will survive)
        await File.WriteAllTextAsync(path2, new string('y', 100 * 1024));
        File.SetLastAccessTimeUtc(path2, DateTime.UtcNow);

        await _cache.EvictIfNeeded();

        // Oldest file should be deleted, newest should survive
        Assert.False(File.Exists(path1), "Oldest file should have been evicted");
        Assert.True(File.Exists(path2), "Newest file should survive eviction");
    }

    [Fact]
    public async Task EvictIfNeeded_NoCacheDir_DoesNotThrow()
    {
        // Use a cache pointing at a non-existent subdirectory that won't be created
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(Path.Combine(_tempDir, "nonexistent-path"));
        var cache = new VideoAudioCache(appPaths.Object, _logger);

        // Should not throw
        await cache.EvictIfNeeded();
    }

    [Fact]
    public void Cleanup_RemovesAllVariantsForItem()
    {
        // Create multiple cache entries for same item (different art ticks)
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);
        string path3 = _cache.GetCacheFilePath("item1", 300);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);

        File.WriteAllText(path1, "v1");
        File.WriteAllText(path2, "v2");
        File.WriteAllText(path3, "v3");

        // Create a different item's cache file — should NOT be deleted
        string otherPath = _cache.GetCacheFilePath("item2", 100);
        File.WriteAllText(otherPath, "other");

        _cache.Cleanup("item1");

        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
        Assert.False(File.Exists(path3));
        Assert.True(File.Exists(otherPath), "Other item's cache should not be touched");
    }

    [Fact]
    public void Cleanup_NoCacheDir_DoesNotThrow()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(Path.Combine(_tempDir, "no-such-dir"));
        var cache = new VideoAudioCache(appPaths.Object, _logger);

        // Should not throw
        cache.Cleanup("any-item");
    }

    [Fact]
    public void CacheDir_UsesCorrectSubdirectory()
    {
        Assert.Contains("alexaskill-video-audio", _cache.CacheDir);
    }
}
