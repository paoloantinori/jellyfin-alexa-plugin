using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for event handlers: PlaybackStarted, Finished, Stopped, Failed,
/// SessionEndedRequest, and ExceptionHandler.
/// </summary>
[Collection("Plugin")]
public class EventHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;

    public EventHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static AudioPlayerRequest CreateAudioPlayerRequest(string type, string? token = null, long offset = 0)
    {
        var request = new AudioPlayerRequest
        {
            Type = type,
            Token = token ?? Guid.NewGuid().ToString(),
            OffsetInMilliseconds = offset
        };
        return request;
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsTrueForPlaybackStarted()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsFalseForOtherTypes()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped");

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackStarted_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", offset: 5000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackFinished_CanHandle_ReturnsTrueForPlaybackFinished()
    {
        var handler = new PlaybackFinishedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackFinished_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackFinishedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", offset: 10000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackStopped_CanHandle_ReturnsTrueForPlaybackStopped()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackStopped_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", offset: 3000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackFailed_CanHandle_ReturnsTrueForPlaybackFailed()
    {
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackFailed_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed");

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.Failed)), Times.Once);
    }

    [Fact]
    public void SessionEnded_CanHandle_ReturnsTrueForSessionEndedRequest()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(new SessionEndedRequest()));
    }

    [Fact]
    public void SessionEnded_CanHandle_ReturnsFalseForIntentRequest()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.False(handler.CanHandle(new IntentRequest()));
    }

    [Fact]
    public async Task SessionEnded_Handle_ReturnsEmpty()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new SessionEndedRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public void ExceptionHandler_CanHandle_ReturnsTrueForSystemExceptionRequest()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(new SystemExceptionRequest()));
    }

    [Fact]
    public void ExceptionHandler_CanHandle_ReturnsFalseForIntentRequest()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.False(handler.CanHandle(new IntentRequest()));
    }

    [Fact]
    public async Task ExceptionHandler_Handle_ReturnsErrorMessage()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new SystemExceptionRequest { Error = new Error { Message = "test error" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("wrong", speech.Text, StringComparison.OrdinalIgnoreCase);
    }
}
