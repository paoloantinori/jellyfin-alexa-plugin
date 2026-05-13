using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
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

/// <summary>
/// Tests that AplVisualsEnabled suppresses APL directives while keeping audio functional.
/// </summary>
[Collection("Plugin")]
public class AplVisualsFeatureFlagTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public AplVisualsFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        _loggerFactory = LoggerFactory.Create(b => { });
        EnsurePluginInstance();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = _config.AplVisualsEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-apl-feature-test-" + Guid.NewGuid());
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
    public void BuildAudioPlayerResponse_NoAplDirective_WhenAplVisualsDisabled()
    {
        _config.AplVisualsEnabled = false;
        Plugin.Instance!.Configuration.AplVisualsEnabled = false;

        var handler = new TestAplHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var context = TestHelpers.CreateContextWithApl();
        var user = TestHelpers.CreateTestUser();
        var itemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Song", Id = itemId };

        var response = handler.TestBuildAudioPlayerResponse(
            global::Alexa.NET.Response.Directive.PlayBehavior.ReplaceAll,
            "http://localhost:8096/Audio/" + itemId + "/stream?static=true",
            itemId.ToString(), item, user, context);

        Assert.NotNull(response);
        // Should have exactly 1 directive: the AudioPlayerPlayDirective (no APL)
        Assert.Single(response.Response.Directives);
        Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(response.Response.Directives[0]);
    }

    [Fact]
    public void BuildAudioPlayerResponse_HasAplDirective_WhenAplVisualsEnabled()
    {
        _config.AplVisualsEnabled = true;
        Plugin.Instance!.Configuration.AplVisualsEnabled = true;

        var handler = new TestAplHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var context = TestHelpers.CreateContextWithApl();
        var user = TestHelpers.CreateTestUser();
        var itemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Song", Id = itemId };

        var response = handler.TestBuildAudioPlayerResponse(
            global::Alexa.NET.Response.Directive.PlayBehavior.ReplaceAll,
            "http://localhost:8096/Audio/" + itemId + "/stream?static=true",
            itemId.ToString(), item, user, context);

        Assert.NotNull(response);
        // Should have 2 directives: AudioPlayerPlayDirective + APL RenderDocument
        Assert.Equal(2, response.Response.Directives.Count);
        Assert.Contains(response.Response.Directives, d => d is global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NoApl_WhenNonAplDevice()
    {
        _config.AplVisualsEnabled = true;
        Plugin.Instance!.Configuration.AplVisualsEnabled = true;

        var handler = new TestAplHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var context = TestHelpers.CreateContextWithoutApl();
        var user = TestHelpers.CreateTestUser();
        var itemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Song", Id = itemId };

        var response = handler.TestBuildAudioPlayerResponse(
            global::Alexa.NET.Response.Directive.PlayBehavior.ReplaceAll,
            "http://localhost:8096/Audio/" + itemId + "/stream?static=true",
            itemId.ToString(), item, user, context);

        Assert.NotNull(response);
        // Non-APL device: only AudioPlayer directive regardless of flag
        Assert.Single(response.Response.Directives);
    }

    private class TestAplHandler : BaseHandler
    {
        public TestAplHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public SkillResponse TestBuildAudioPlayerResponse(
            global::Alexa.NET.Response.Directive.PlayBehavior playBehavior,
            string streamUrl, string itemId,
            MediaBrowser.Controller.Entities.BaseItem item,
            Entities.User user, Context context)
            => BuildAudioPlayerResponse(playBehavior, streamUrl, itemId, item, user, context);
    }
}

/// <summary>
/// Tests for BaseHandler library filtering methods (GetAllowedLibraryIds, ApplyLibraryFilter).
/// </summary>
public class LibraryFilterTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestLibraryFilterHandler _handler;

    public LibraryFilterTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestLibraryFilterHandler(_sessionManagerMock.Object, _config, _loggerFactory);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsNull_WhenNull()
    {
        var user = new Entities.User { AllowedLibraryIds = null };

        var result = _handler.TestGetAllowedLibraryIds(user);

        Assert.Null(result);
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsNull_WhenEmpty()
    {
        var user = new Entities.User { AllowedLibraryIds = new List<string>() };

        var result = _handler.TestGetAllowedLibraryIds(user);

        Assert.Null(result);
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsGuids_WhenValid()
    {
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { id1, id2 } };

        var result = _handler.TestGetAllowedLibraryIds(user);

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal(Guid.Parse(id1), result[0]);
        Assert.Equal(Guid.Parse(id2), result[1]);
    }

    [Fact]
    public void GetAllowedLibraryIds_SkipsInvalidGuids()
    {
        var validId = Guid.NewGuid().ToString();
        var user = new Entities.User
        {
            AllowedLibraryIds = new List<string> { "not-a-guid", validId, "also-invalid" }
        };

        var result = _handler.TestGetAllowedLibraryIds(user);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(Guid.Parse(validId), result[0]);
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsNull_WhenAllInvalid()
    {
        var user = new Entities.User
        {
            AllowedLibraryIds = new List<string> { "bad", "also-bad" }
        };

        var result = _handler.TestGetAllowedLibraryIds(user);

        Assert.Null(result);
    }

    [Fact]
    public void ApplyLibraryFilter_SetsTopParentIds_WhenHasLibraries()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var user = new Entities.User
        {
            AllowedLibraryIds = new List<string> { id1.ToString(), id2.ToString() }
        };
        var query = new InternalItemsQuery();

        _handler.TestApplyLibraryFilter(query, user);

        Assert.NotNull(query.TopParentIds);
        Assert.Equal(2, query.TopParentIds.Length);
        Assert.Contains(id1, query.TopParentIds);
        Assert.Contains(id2, query.TopParentIds);
    }

    [Fact]
    public void ApplyLibraryFilter_DoesNotSetTopParentIds_WhenNull()
    {
        var user = new Entities.User { AllowedLibraryIds = null };
        var query = new InternalItemsQuery();

        _handler.TestApplyLibraryFilter(query, user);

        // TopParentIds is not overwritten when user has no library filter.
        // InternalItemsQuery initializes it to an empty array, so verify it remains empty.
        Assert.Empty(query.TopParentIds);
    }

    private class TestLibraryFilterHandler : BaseHandler
    {
        public TestLibraryFilterHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public Guid[]? TestGetAllowedLibraryIds(Entities.User user)
            => GetAllowedLibraryIds(user);

        public void TestApplyLibraryFilter(InternalItemsQuery query, Entities.User user)
            => ApplyLibraryFilter(query, user);
    }
}
