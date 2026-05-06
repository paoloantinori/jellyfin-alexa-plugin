using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
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
    public async Task PauseIntentHandler_Handle_ReturnsAudioPlayerStop()
    {
        var handler = new PauseIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

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

    private static Context CreateContext() => TestHelpers.CreateTestContext();
}
