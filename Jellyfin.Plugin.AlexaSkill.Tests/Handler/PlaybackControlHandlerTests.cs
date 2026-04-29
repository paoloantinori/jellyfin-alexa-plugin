using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for simple playback control handlers: Pause, Fallback.
/// </summary>
public class PlaybackControlHandlerTests
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
    public void PauseIntentHandler_Handle_ReturnsAudioPlayerStop()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = handler.Handle(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public void FallbackIntentHandler_CanHandle_MatchesFallbackIntent()
    {
        var handler = new FallbackIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.FallbackIntent" } };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void FallbackIntentHandler_Handle_ReturnsFallbackMessage()
    {
        var handler = new FallbackIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = handler.Handle(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.FallbackIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("could not understand", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static Context CreateContext()
    {
        return new Context
        {
            System = new Alexa.NET.Request.System
            {
                User = new Alexa.NET.Request.User
                {
                    AccessToken = Guid.NewGuid().ToString()
                },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }
}
