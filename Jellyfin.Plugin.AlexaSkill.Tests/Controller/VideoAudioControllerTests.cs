using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// Tests for the VideoAudioController — MP4 video generation endpoint
/// that combines album art + audio via ffmpeg for Alexa Echo Show VideoApp playback.
/// </summary>
[Collection("Plugin")]
public class VideoAudioControllerTests : PluginTestBase, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IMediaEncoder> _mediaEncoderMock;
    private readonly VideoAudioCache _cache;
    private readonly string _tempDir;
    private readonly PluginConfiguration _config;

    public VideoAudioControllerTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mediaEncoderMock = new Mock<IMediaEncoder>();

        // Create a temp dir for the cache service used by controller tests
        _tempDir = Path.Combine(Path.GetTempPath(), "va-ctrl-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);

        var cacheLogger = _loggerFactory.CreateLogger<VideoAudioCache>();
        _cache = new VideoAudioCache(appPaths.Object, cacheLogger);

        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg => { },
            "video-audio-test");

        _config = Plugin.Instance!.Configuration;
        _config.ServerAddress = "http://localhost:8096";
    }

    public void Dispose()
    {
        _config.ServerAddress = string.Empty;

        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verify that the endpoint returns 400 when itemId is not a valid GUID.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamVideoAudio("not-a-guid", null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// Verify that the endpoint returns 404 when the item is not found.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_ItemNotFound_Returns404()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var controller = CreateController();
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(
            Guid.NewGuid().ToString(),
            null);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments produces correct arguments with album art URL,
    /// including codec, filter, and streaming flags.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_WithArtUrl_ContainsAllExpectedFlags()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";

        string args = VideoAudioController.BuildFfmpegArguments(artUrl, audioUrl, false);

        Assert.Contains("-loop 1", args, StringComparison.Ordinal);
        Assert.Contains(artUrl, args, StringComparison.Ordinal);
        Assert.Contains(audioUrl, args, StringComparison.Ordinal);
        Assert.Contains("libx264", args, StringComparison.Ordinal);
        Assert.Contains("-tune stillimage", args, StringComparison.Ordinal);
        Assert.Contains("-preset ultrafast", args, StringComparison.Ordinal);
        Assert.Contains("scale=1280:720", args, StringComparison.Ordinal);
        Assert.Contains("pad=1280:720", args, StringComparison.Ordinal);
        Assert.Contains("-c:a aac", args, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt yuv420p", args, StringComparison.Ordinal);
        Assert.Contains("frag_keyframe+empty_moov", args, StringComparison.Ordinal);
        Assert.Contains("-shortest", args, StringComparison.Ordinal);
        Assert.Contains("pipe:1", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-f lavfi", args, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments produces correct arguments with black frame fallback.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_BlackFrame_ContainsLavfiInput()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";

        string args = VideoAudioController.BuildFfmpegArguments(null, audioUrl, true);

        Assert.Contains("-f lavfi", args, StringComparison.Ordinal);
        Assert.Contains("color=c=black:s=1280x720", args, StringComparison.Ordinal);
        Assert.Contains(audioUrl, args, StringComparison.Ordinal);
        Assert.DoesNotContain("-loop 1", args, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verify that the cache directory is created and the cache file path follows
    /// the expected pattern with itemId and art modification ticks.
    /// </summary>
    [Fact]
    public void CacheFilePath_Format_ContainsExpectedComponents()
    {
        string path = _cache.GetCacheFilePath("abc123def456", 637500000000000000L);

        Assert.Contains("abc123def456", Path.GetFileName(path));
        Assert.Contains("637500000000000000", Path.GetFileName(path));
        Assert.EndsWith(".mp4", path);
    }

    /// <summary>
    /// Verify that a cached file is detected as a cache hit.
    /// </summary>
    [Fact]
    public async Task CacheHit_ReturnsExistingFile()
    {
        string path = _cache.GetCacheFilePath("test-item", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "cached content");

        var result = await _cache.GetCachedFile("test-item", 12345);

        Assert.NotNull(result);
        Assert.Equal(path, result!.FullName);
    }

    /// <summary>
    /// Verify that a cache miss returns null.
    /// </summary>
    [Fact]
    public async Task CacheMiss_ReturnsNull()
    {
        var result = await _cache.GetCachedFile("nonexistent", 0);

        Assert.Null(result);
    }

    /// <summary>
    /// Verify that cleanup removes all cached files for a given item.
    /// </summary>
    [Fact]
    public void Cleanup_RemovesAllVariants()
    {
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);
        File.WriteAllText(path1, "v1");
        File.WriteAllText(path2, "v2");

        _cache.Cleanup("item1");

        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
    }

    private VideoAudioController CreateController()
    {
        return new VideoAudioController(
            _libraryManagerMock.Object,
            _mediaEncoderMock.Object,
            _cache,
            _loggerFactory);
    }
}
