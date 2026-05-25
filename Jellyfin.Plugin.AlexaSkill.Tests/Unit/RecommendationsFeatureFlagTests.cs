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
/// Tests that RecommendIntentHandler respects the RecommendationsEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class RecommendationsFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;

    public RecommendationsFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.RecommendationsEnabled = _config.RecommendationsEnabled,
            "alexa-recommendations-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task Recommend_ReturnsDisabledMessage_WhenRecommendationsDisabled()
    {
        _config.RecommendationsEnabled = false;
        Plugin.Instance!.Configuration.RecommendationsEnabled = false;

        var handler = new RecommendIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object,
            _userDataManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "RecommendIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recommend_ProceedsNormally_WhenRecommendationsEnabled()
    {
        var handler = new RecommendIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object,
            _userDataManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "RecommendIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Proceeds past the feature check — returns a user/login error, not "disabled"
        Assert.DoesNotContain("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }
}
