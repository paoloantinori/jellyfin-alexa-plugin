using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests that PlayVideoIntentHandler and PlayEpisodeIntentHandler respect the
/// VideoPlaybackEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class VideoPlaybackFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;

    public VideoPlaybackFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.VideoPlaybackEnabled = _config.VideoPlaybackEnabled,
            "alexa-video-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task PlayVideo_ReturnsDisabledMessage_WhenVideoPlaybackDisabled()
    {
        _config.VideoPlaybackEnabled = false;
        Plugin.Instance!.Configuration.VideoPlaybackEnabled = false;

        var handler = new PlayVideoIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayVideoIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayEpisode_ReturnsDisabledMessage_WhenVideoPlaybackDisabled()
    {
        _config.VideoPlaybackEnabled = false;
        Plugin.Instance!.Configuration.VideoPlaybackEnabled = false;

        var handler = new PlayEpisodeIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayEpisodeIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayVideo_ProceedsNormally_WhenVideoPlaybackEnabled()
    {
        var handler = new PlayVideoIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayVideoIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Without a title slot, handler returns "I didn't catch the video title"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("video title", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayEpisode_ProceedsNormally_WhenVideoPlaybackEnabled()
    {
        var handler = new PlayEpisodeIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayEpisodeIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Without a series slot, handler returns "I didn't catch the series name"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("series name", text, StringComparison.OrdinalIgnoreCase);
    }
}
