using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for feature flag defaults in PluginConfiguration.
/// </summary>
public class PluginConfigurationFeatureFlagTests
{
    [Fact]
    public void FeatureFlags_DefaultToTrue()
    {
        var config = new PluginConfiguration();

        Assert.True(config.RadioModeEnabled);
        Assert.True(config.PodcastsEnabled);
        Assert.True(config.LiveTvEnabled);
        Assert.True(config.SleepTimerEnabled);
        Assert.True(config.QueueManagementEnabled);
        Assert.True(config.BrowseLibraryEnabled);
        Assert.True(config.RecommendationsEnabled);
        Assert.True(config.AplVisualsEnabled);
        Assert.True(config.VideoPlaybackEnabled);
    }

    [Fact]
    public void FeatureFlags_CanBeSetToFalse()
    {
        var config = new PluginConfiguration
        {
            RadioModeEnabled = false,
            PodcastsEnabled = false,
            LiveTvEnabled = false,
            SleepTimerEnabled = false,
            QueueManagementEnabled = false,
            BrowseLibraryEnabled = false,
            RecommendationsEnabled = false,
            AplVisualsEnabled = false,
            VideoPlaybackEnabled = false
        };

        Assert.False(config.RadioModeEnabled);
        Assert.False(config.PodcastsEnabled);
        Assert.False(config.LiveTvEnabled);
        Assert.False(config.SleepTimerEnabled);
        Assert.False(config.QueueManagementEnabled);
        Assert.False(config.BrowseLibraryEnabled);
        Assert.False(config.RecommendationsEnabled);
        Assert.False(config.AplVisualsEnabled);
        Assert.False(config.VideoPlaybackEnabled);
    }
}

/// <summary>
/// Tests for BaseHandler.IfFeatureDisabled method via a concrete test handler.
/// </summary>
[Collection("Plugin")]
public class IfFeatureDisabledTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestFeatureFlagHandler _handler;

    public IfFeatureDisabledTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestFeatureFlagHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        EnsurePluginInstance();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Sets Plugin.Instance with our test configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration.
    /// </summary>
    private void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.RadioModeEnabled = _config.RadioModeEnabled;
            Plugin.Instance.Configuration.SleepTimerEnabled = _config.SleepTimerEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-feature-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(_config);

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    [Fact]
    public void IfFeatureDisabled_ReturnsNull_WhenFeatureEnabled()
    {
        // All flags default to true
        var request = new IntentRequest { Intent = new Intent { Name = "TestIntent" } };

        var result = _handler.TestIfFeatureDisabled(c => c.RadioModeEnabled, request);

        Assert.Null(result);
    }

    [Fact]
    public void IfFeatureDisabled_ReturnsResponse_WhenFeatureDisabled()
    {
        _config.RadioModeEnabled = false;
        Plugin.Instance!.Configuration.RadioModeEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "TestIntent" } };

        var result = _handler.TestIfFeatureDisabled(c => c.RadioModeEnabled, request);

        Assert.NotNull(result);
    }

    [Fact]
    public void IfFeatureDisabled_ReturnsResponse_WithFeatureDisabledMessage()
    {
        _config.SleepTimerEnabled = false;
        Plugin.Instance!.Configuration.SleepTimerEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "TestIntent" } };

        var result = _handler.TestIfFeatureDisabled(c => c.SleepTimerEnabled, request);

        Assert.NotNull(result);
        var text = TestHelpers.GetSpeechText(result);
        Assert.Contains("disabled", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feature", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal concrete handler to expose the protected IfFeatureDisabled for testing.
    /// </summary>
    private class TestFeatureFlagHandler : BaseHandler
    {
        public TestFeatureFlagHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public SkillResponse? TestIfFeatureDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
            => IfFeatureDisabled(isEnabled, request);
    }
}

/// <summary>
/// Tests that real handlers respect feature flags.
/// </summary>
[Collection("Plugin")]
public class HandlerFeatureFlagTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public HandlerFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        EnsurePluginInstance();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Sets Plugin.Instance with our test configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration.
    /// </summary>
    private void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.RadioModeEnabled = _config.RadioModeEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-handler-feature-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(_config);

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task PlayRadio_ReturnsDisabledMessage_WhenRadioModeDisabled()
    {
        _config.RadioModeEnabled = false;
        Plugin.Instance!.Configuration.RadioModeEnabled = false;
        var handler = new PlayRadioIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("disabled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayRadio_ProceedsNormally_WhenRadioModeEnabled()
    {
        // RadioModeEnabled defaults to true
        var handler = new PlayRadioIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        // With radio enabled but nothing playing, should get the "nothing playing" message,
        // NOT the feature disabled message.
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }
}
