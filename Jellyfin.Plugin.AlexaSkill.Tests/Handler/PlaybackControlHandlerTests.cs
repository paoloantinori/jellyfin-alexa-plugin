using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for pause/stop/cancel handlers.
/// Regression guards for:
/// - AudioPlayer.Stop directive must be present on ALL paths (stop, cancel, pause)
/// - ShouldEndSession must be true on all paths
/// - No OutputSpeech on pause (causes device to ignore Stop directive)
/// - AudioPlayer.Play responses must use ShouldEndSession=true
/// </summary>
[Collection("Plugin")]
public class PlaybackControlHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlaybackControlHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    [Theory]
    [InlineData("AMAZON.PauseIntent", true)]
    [InlineData("AMAZON.StopIntent", true)]
    [InlineData("AMAZON.CancelIntent", true)]
    [InlineData("AMAZON.ResumeIntent", false)]
    public void PauseIntentHandler_CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public void PauseIntentHandler_CanHandle_HardwarePauseButton()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        // PlaybackControllerRequest.PlaybackRequestType is read-only; deserialize from JSON
        var json = @"{""requestId"":""test"",""type"":""PlaybackController.PauseCommandIssued"",""timestamp"":""2024-01-01T00:00:00Z"",""locale"":""en-US"",""playbackRequestMethod"":""PAUSE""}";
        var request = JsonConvert.DeserializeObject<PlaybackControllerRequest>(json);

        Assert.NotNull(request);
        Assert.True(handler.CanHandle(request));
    }

    // === Regression: all paths must include AudioPlayer.Stop directive ===

    [Fact]
    public async Task PauseIntentHandler_Pause_ReturnsAudioPlayerStopDirective()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        AssertHasAudioPlayerStopDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task PauseIntentHandler_Stop_ReturnsAudioPlayerStopDirective()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.StopIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        AssertHasAudioPlayerStopDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.Card);
    }

    [Fact]
    public async Task PauseIntentHandler_Cancel_ReturnsAudioPlayerStopDirective()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.CancelIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        AssertHasAudioPlayerStopDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.Card);
    }

    // === Regression: pause must never include OutputSpeech ===

    [Fact]
    public async Task PauseIntentHandler_Pause_NeverIncludesOutputSpeech()
    {
        _config.SeekEnabled = true;
        _config.PauseAnnouncePosition = true;
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => { c.SeekEnabled = true; c.PauseAnnouncePosition = true; },
            "pause-no-speech-test");

        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var session = CreateSession();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            RunTimeTicks = 180 * TimeSpan.TicksPerSecond
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session, CancellationToken.None);

        // OutputSpeech must be null — speaking causes the Echo to ignore the Stop directive
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        AssertHasAudioPlayerStopDirective(response);
    }

    // === Regression: position card only when both seek + announce enabled ===

    [Fact]
    public async Task PauseIntentHandler_Pause_PositionCard_WhenSeekAndAnnounceEnabled()
    {
        _config.SeekEnabled = true;
        _config.PauseAnnouncePosition = true;
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => { c.SeekEnabled = true; c.PauseAnnouncePosition = true; },
            "pause-card-test");

        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var session = CreateSession();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            RunTimeTicks = 180 * TimeSpan.TicksPerSecond
        };
        // BuildPositionDisplay uses NowPlayingItem.RunTimeTicks and PlayState.PositionTicks
        session.NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto
        {
            Name = "Test Song",
            RunTimeTicks = 180 * TimeSpan.TicksPerSecond
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = 30 * TimeSpan.TicksPerSecond
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session, CancellationToken.None);

        Assert.NotNull(response.Response.Card);
        Assert.IsType<StandardCard>(response.Response.Card);
        var card = (StandardCard)response.Response.Card;
        Assert.Equal("Test Song", card.Title);
        Assert.Contains("30 second", card.Content);
    }

    [Fact]
    public async Task PauseIntentHandler_Pause_NoCard_WhenAnnouncePositionOff()
    {
        _config.SeekEnabled = true;
        _config.PauseAnnouncePosition = false;
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => { c.SeekEnabled = true; c.PauseAnnouncePosition = false; },
            "pause-announce-off-test");

        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var session = CreateSession();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            RunTimeTicks = 180 * TimeSpan.TicksPerSecond
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session, CancellationToken.None);

        Assert.Null(response.Response.Card);
    }

    [Fact]
    public async Task PauseIntentHandler_Pause_NoCard_WhenNoNowPlayingItem()
    {
        _config.SeekEnabled = true;
        _config.PauseAnnouncePosition = true;
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => { c.SeekEnabled = true; c.PauseAnnouncePosition = true; },
            "pause-no-item-test");

        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.Null(response.Response.Card);
    }

    // === Regression: ShouldEndSession=true when device already stopped ===

    [Fact]
    public async Task PauseIntentHandler_DeviceAlreadyStopped_EndsSession()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var context = TestHelpers.CreateTestContext();
        context.AudioPlayer = new PlaybackState
        {
            PlayerActivity = "STOPPED",
            Token = "test-token"
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        AssertHasAudioPlayerStopDirective(response);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task PauseIntentHandler_DeviceFinished_EndsSession()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var context = TestHelpers.CreateTestContext();
        context.AudioPlayer = new PlaybackState
        {
            PlayerActivity = "FINISHED",
            Token = "test-token"
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        AssertHasAudioPlayerStopDirective(response);
        Assert.True(response.Response.ShouldEndSession);
    }

    // === BuildPauseResponse regression ===

    [Fact]
    public void BuildPauseResponse_EndsSession_WithAudioPlayerStop()
    {
        var response = BaseHandler.BuildPauseResponse();

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        AssertHasAudioPlayerStopDirective(response);
    }

    // === Fallback tests ===

    [Fact]
    public void FallbackIntentHandler_CanHandle_MatchesFallbackIntent()
    {
        var handler = new FallbackIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.FallbackIntent" } };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task FallbackIntentHandler_Handle_ReturnsFallbackMessage()
    {
        var handler = new FallbackIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.FallbackIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("could not understand", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    // === Helpers ===

    /// <summary>
    /// Assert that the response contains a StopDirective (AudioPlayer.Stop).
    /// Regression guard: ResponseBuilder.Empty() does NOT include this directive,
    /// so audio continues playing after stop/cancel commands.
    /// </summary>
    private static void AssertHasAudioPlayerStopDirective(SkillResponse response)
    {
        Assert.NotNull(response.Response.Directives);
        Assert.Contains(response.Response.Directives, d => d is StopDirective);
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
}
