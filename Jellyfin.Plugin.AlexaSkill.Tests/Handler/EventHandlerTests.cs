using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for event handlers: PlaybackStarted, Finished, Stopped, Failed,
/// SessionEndedRequest, and ExceptionHandler.
/// </summary>
public class EventHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public EventHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static Context CreateContext()
    {
        return new Context
        {
            System = new Alexa.NET.Request.System
            {
                User = new Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }

    private static SessionInfo CreateSession()
    {
        return new SessionInfo
        {
            PlayState = new PlayerStateInfo()
        };
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsTrueForPlaybackStarted()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest { AudioRequestType = AudioRequestType.PlaybackStarted };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsFalseForOtherTypes()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest { AudioRequestType = AudioRequestType.PlaybackStopped };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackStarted_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            AudioRequestType = AudioRequestType.PlaybackStarted,
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 5000
        };

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession());

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackFinished_CanHandle_ReturnsTrueForPlaybackFinished()
    {
        var handler = new PlaybackFinishedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest { AudioRequestType = AudioRequestType.PlaybackFinished };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackFinished_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackFinishedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            AudioRequestType = AudioRequestType.PlaybackFinished,
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 10000
        };

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession());

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackStopped_CanHandle_ReturnsTrueForPlaybackStopped()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest { AudioRequestType = AudioRequestType.PlaybackStopped };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackStopped_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            AudioRequestType = AudioRequestType.PlaybackStopped,
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 3000
        };

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession());

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackFailed_CanHandle_ReturnsTrueForPlaybackFailed()
    {
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest { AudioRequestType = AudioRequestType.PlaybackFailed };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackFailed_Handle_ReturnsErrorMessage()
    {
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            AudioRequestType = AudioRequestType.PlaybackFailed,
            Token = Guid.NewGuid().ToString()
        };

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("wrong", speech.Text, StringComparison.OrdinalIgnoreCase);
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
    public void SessionEnded_Handle_ReturnsEmpty()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = handler.Handle(
            new SessionEndedRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession());

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
    public void ExceptionHandler_Handle_ReturnsErrorMessage()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = handler.Handle(
            new SystemExceptionRequest { Error = new Error { Message = "test error" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("wrong", speech.Text, StringComparison.OrdinalIgnoreCase);
    }
}
