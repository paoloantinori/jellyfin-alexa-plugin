using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
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
    private readonly PluginConfiguration _config;

    public VideoAudioControllerTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mediaEncoderMock = new Mock<IMediaEncoder>();

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
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verify that the endpoint returns 401 when no API key is provided.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_NoApiKey_Returns401()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamVideoAudio(Guid.NewGuid().ToString(), null);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(unauthorized.Value);
    }

    /// <summary>
    /// Verify that the endpoint returns 401 when the API key format is invalid
    /// (contains non-hex characters that could be a command injection vector).
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_InvalidApiKeyFormat_Returns401()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamVideoAudio(
            Guid.NewGuid().ToString(),
            "not-valid-chars!@#");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(unauthorized.Value);
    }

    /// <summary>
    /// Verify that the endpoint returns 400 when itemId is not a valid GUID.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamVideoAudio("not-a-guid", "abc123def456");

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
            "abc123def456abc123def456abc12345");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    /// <summary>
    /// Verify that IsValidApiKey returns true for valid hex API keys.
    /// </summary>
    [Theory]
    [InlineData("abc123def456abc123def456abc12345", true)]
    [InlineData("ABCDEF1234567890", true)]
    [InlineData("0123456789abcdef", true)]
    [InlineData("", false)]
    [InlineData("not-hex-chars!", false)]
    [InlineData("validhexbut has space", false)]
    [InlineData("key;rm -rf /", false)]
    [InlineData("key\"injection", false)]
    [InlineData("key'injection", false)]
    public void IsValidApiKey_ValidatesCorrectly(string apiKey, bool expected)
    {
        Assert.Equal(expected, VideoAudioController.IsValidApiKey(apiKey));
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments produces correct arguments with album art URL,
    /// including codec, filter, and streaming flags.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_WithArtUrl_ContainsAllExpectedFlags()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary?api_key=abc";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true&api_key=abc";

        string args = VideoAudioController.BuildFfmpegArguments(artUrl, audioUrl, false);

        Assert.Contains("-loop 1", args, StringComparison.Ordinal);
        Assert.Contains(artUrl, args, StringComparison.Ordinal);
        Assert.Contains(audioUrl, args, StringComparison.Ordinal);
        Assert.Contains("libx264", args, StringComparison.Ordinal);
        Assert.Contains("-tune stillimage", args, StringComparison.Ordinal);
        Assert.Contains("-preset ultrafast", args, StringComparison.Ordinal);
        Assert.Contains("scale=1280:720", args, StringComparison.Ordinal);
        Assert.Contains("pad=1280:720", args, StringComparison.Ordinal);
        Assert.Contains("-c:a copy", args, StringComparison.Ordinal);
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
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true&api_key=abc";

        string args = VideoAudioController.BuildFfmpegArguments(null, audioUrl, true);

        Assert.Contains("-f lavfi", args, StringComparison.Ordinal);
        Assert.Contains("color=c=black:s=1280x720", args, StringComparison.Ordinal);
        Assert.Contains(audioUrl, args, StringComparison.Ordinal);
        Assert.DoesNotContain("-loop 1", args, StringComparison.Ordinal);
    }

    private VideoAudioController CreateController()
    {
        return new VideoAudioController(
            _libraryManagerMock.Object,
            _mediaEncoderMock.Object,
            _loggerFactory);
    }
}
