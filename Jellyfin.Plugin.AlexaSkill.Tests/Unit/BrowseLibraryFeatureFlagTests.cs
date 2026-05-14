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
/// Tests that BrowseLibraryIntentHandler respects the BrowseLibraryEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class BrowseLibraryFeatureFlagTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public BrowseLibraryFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.BrowseLibraryEnabled = _config.BrowseLibraryEnabled,
            "alexa-browse-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task BrowseLibrary_ReturnsDisabledMessage_WhenBrowseLibraryDisabled()
    {
        _config.BrowseLibraryEnabled = false;
        Plugin.Instance!.Configuration.BrowseLibraryEnabled = false;

        var handler = new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "BrowseLibraryIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowseLibrary_ProceedsNormally_WhenBrowseLibraryEnabled()
    {
        var handler = new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "BrowseLibraryIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Without a browse category slot, handler returns "What would you like to browse?"
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("browse", text, StringComparison.OrdinalIgnoreCase);
    }
}
