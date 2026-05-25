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
/// Tests that radio mode handlers respect the RadioModeEnabled feature flag.
/// Covers PlayRadioIntentHandler, TurnRadioOnIntentHandler, and TurnRadioOffIntentHandler.
/// </summary>
[Collection("Plugin")]
public class RadioModeFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public RadioModeFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.RadioModeEnabled = _config.RadioModeEnabled,
            "alexa-radio-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    // --- Disabled tests ---

    [Fact]
    public async Task PlayRadio_ReturnsDisabledMessage_WhenRadioModeDisabled()
    {
        _config.RadioModeEnabled = false;
        Plugin.Instance!.Configuration.RadioModeEnabled = false;

        var handler = new PlayRadioIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOn_ReturnsDisabledMessage_WhenRadioModeDisabled()
    {
        _config.RadioModeEnabled = false;
        Plugin.Instance!.Configuration.RadioModeEnabled = false;

        var handler = new TurnRadioOnIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOff_ReturnsDisabledMessage_WhenRadioModeDisabled()
    {
        _config.RadioModeEnabled = false;
        Plugin.Instance!.Configuration.RadioModeEnabled = false;

        var handler = new TurnRadioOffIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    // --- Enabled (normal behavior) tests ---

    [Fact]
    public async Task PlayRadio_ProceedsNormally_WhenRadioModeEnabled()
    {
        var handler = new PlayRadioIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        // With radio enabled but nothing playing, returns "Nothing is currently playing"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOn_ProceedsNormally_WhenRadioModeEnabled()
    {
        var handler = new TurnRadioOnIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Returns "Radio mode is now on"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOff_ProceedsNormally_WhenRadioModeEnabled()
    {
        var handler = new TurnRadioOffIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Returns "Radio mode is now off"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("off", text, StringComparison.OrdinalIgnoreCase);
    }
}
