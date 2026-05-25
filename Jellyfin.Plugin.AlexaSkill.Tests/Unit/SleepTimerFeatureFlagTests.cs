using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests that SleepTimerIntentHandler respects the SleepTimerEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class SleepTimerFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SleepTimerFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SleepTimerEnabled = _config.SleepTimerEnabled,
            "alexa-sleep-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static IntentRequest CreateSleepTimerRequest(string minutes)
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "SleepTimerIntent",
                Slots = new Dictionary<string, Slot>
                {
                    { "duration_minutes", new Slot { Value = minutes } }
                }
            }
        };
    }

    [Fact]
    public async Task SleepTimer_ReturnsDisabledMessage_WhenSleepTimerDisabled()
    {
        _config.SleepTimerEnabled = false;
        Plugin.Instance!.Configuration.SleepTimerEnabled = false;

        var handler = new SleepTimerIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            CreateSleepTimerRequest("30"),
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SleepTimer_ProceedsNormally_WhenSleepTimerEnabled()
    {
        var handler = new SleepTimerIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreateSleepTimerRequest("30"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        // With sleep timer enabled but nothing playing, returns "Nothing is currently playing"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("playing", text, StringComparison.OrdinalIgnoreCase);
    }
}
