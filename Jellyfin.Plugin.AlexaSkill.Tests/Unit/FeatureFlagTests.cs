using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
        Assert.True(config.ResumeOfferEnabled);
        Assert.True(config.ResumeAnnounceTitle);
        Assert.True(config.AsrCompoundWordFixEnabled);

        // SeekEnabled defaults to false (opt-in feature)
        Assert.False(config.SeekEnabled);
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
            VideoPlaybackEnabled = false,
            ResumeOfferEnabled = false,
            ResumeAnnounceTitle = false,
            AsrCompoundWordFixEnabled = false,
            SeekEnabled = true
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
        Assert.False(config.ResumeOfferEnabled);
        Assert.False(config.ResumeAnnounceTitle);
        Assert.False(config.AsrCompoundWordFixEnabled);

        // SeekEnabled can be set to true (opt-in)
        Assert.True(config.SeekEnabled);
    }
}

/// <summary>
/// Tests for BaseHandler.IfFeatureDisabled method via a concrete test handler.
/// </summary>
[Collection("Plugin")]
public class IfFeatureDisabledTests : PluginTestBase, IDisposable
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
public class HandlerFeatureFlagTests : PluginTestBase, IDisposable
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
        // With radio enabled, nothing playing, and an empty station slot, the handler
        // proceeds normally into the JF-472 station elicit, NOT the feature disabled
        // message.
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Tests that AplVisualsEnabled suppresses APL directives while keeping audio functional.
/// </summary>
[Collection("Plugin")]
public class AplVisualsFeatureFlagTests : PluginTestBase, IDisposable
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
    public void BuildAudioPlayerResponse_NoAplDirective_WhenAplVisualsEnabled()
    {
        // APL NowPlaying overlay was removed — Echo's built-in player takes
        // visual priority, so BuildAudioPlayerResponse only emits AudioPlayer.
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
        // No APL directive even when visuals enabled — overlay removed
        Assert.Single(response.Response.Directives);
        Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(response.Response.Directives[0]);
        Assert.True(response.Response.ShouldEndSession);
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
/// Tests for library filtering (LibraryFilter.GetAllowedLibraryIds, ApplyLibraryFilter).
/// </summary>
public class LibraryFilterTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestLibraryFilterHandler _handler;

    public LibraryFilterTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
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

        var result = LibraryFilter.GetAllowedLibraryIds(user);

        Assert.Null(result);
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsNull_WhenEmpty()
    {
        var user = new Entities.User { AllowedLibraryIds = new List<string>() };

        var result = LibraryFilter.GetAllowedLibraryIds(user);

        Assert.Null(result);
    }

    [Fact]
    public void GetAllowedLibraryIds_ReturnsGuids_WhenValid()
    {
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { id1, id2 } };

        var result = LibraryFilter.GetAllowedLibraryIds(user);

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

        var result = LibraryFilter.GetAllowedLibraryIds(user);

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

        var result = LibraryFilter.GetAllowedLibraryIds(user);

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

        _handler.TestApplyLibraryFilter(query, user, _libraryManagerMock.Object);

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

        _handler.TestApplyLibraryFilter(query, user, _libraryManagerMock.Object);

        // TopParentIds is not overwritten when user has no library filter.
        // InternalItemsQuery initializes it to an empty array, so verify it remains empty.
        Assert.Empty(query.TopParentIds);
    }

    // --- JF-456: kind-aware library exemption (single decision point) ---

    [Fact]
    public void ApplyLibraryFilter_SkipsTopParentIds_WhenAllKindsAreOutOfLibrary()
    {
        // GH #22 residuals: playlists and live-TV channels live outside every media
        // library, so a TopParentIds filter can only exclude them entirely. The
        // kind-aware ApplyLibraryFilter must skip the filter for all-exempt queries
        // even when the user IS restricted.
        var libId = Guid.NewGuid();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } };

        var playlistQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist }
        };
        var channelQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel }
        };

        LibraryFilter.ApplyLibraryFilter(playlistQuery, user, _libraryManagerMock.Object);
        LibraryFilter.ApplyLibraryFilter(channelQuery, user, _libraryManagerMock.Object);

        Assert.Empty(playlistQuery.TopParentIds);
        Assert.Empty(channelQuery.TopParentIds);
    }

    [Fact]
    public void ApplyLibraryFilter_KeepsTopParentIds_ForMixedKinds()
    {
        // Mixed kind sets keep the filter: the in-library rows are the point of the
        // query, and the out-of-library kinds need their own sibling query (the
        // handler's job), not a silently dropped filter.
        var libId = Guid.NewGuid();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } };
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio, Jellyfin.Data.Enums.BaseItemKind.Playlist }
        };

        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManagerMock.Object);

        Assert.Contains(libId, query.TopParentIds);
    }

    [Fact]
    public void ApplyLibraryFilter_KeepsTopParentIds_WhenNoKindFilter()
    {
        // A null/empty IncludeItemTypes means "all kinds" to Jellyfin; the filter applies.
        var libId = Guid.NewGuid();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } };
        var query = new InternalItemsQuery();

        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManagerMock.Object);

        Assert.Contains(libId, query.TopParentIds);
    }

    // --- JF-456 deep fix (code-review round 2 item 1): automatic items-by-name bypass ---

    [Fact]
    public void ApplyLibraryFilter_SetsIncludeItemsByName_ForParametricMusicArtistKind()
    {
        // Regression for the BrowseLibrary miss: QueryItems builds IncludeItemTypes
        // from a VARIABLE kind (SlotMappings maps the "artists" browse category to
        // MusicArtist), a shape no per-site wiring can recognize by literal kind.
        // The bypass must come from ApplyLibraryFilter itself.
        var libId = Guid.NewGuid();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } };

        Jellyfin.Data.Enums.BaseItemKind itemType = SlotMappings.BrowseCategoryToItemKind["artists"]!.Value;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { itemType }
        };

        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManagerMock.Object);

        // Folderless artists carry NULL TopParentId: without the bypass a restricted
        // DB artist query matches zero rows in the cold-index window.
        Assert.True(query.IncludeItemsByName, "restricted MusicArtist queries must get the items-by-name bypass automatically");
        Assert.Contains(libId, query.TopParentIds);
    }

    [Fact]
    public void ApplyLibraryFilter_IncludeItemsByNameBypass_GatesAndOptOut()
    {
        var libId = Guid.NewGuid();
        var user = new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } };

        // Non-artist kind: no bypass (Jellyfin only evaluates it for item-by-name types).
        var albumQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicAlbum }
        };
        LibraryFilter.ApplyLibraryFilter(albumQuery, user, _libraryManagerMock.Object);
        Assert.False(albumQuery.IncludeItemsByName == true);

        // Opt-out (the strict catalog surfaces): no bypass even for MusicArtist,
        // but the TopParentIds scope is still applied.
        var strictQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicArtist }
        };
        LibraryFilter.ApplyLibraryFilter(strictQuery, user, _libraryManagerMock.Object, includeItemsByName: false);
        Assert.False(strictQuery.IncludeItemsByName == true);
        Assert.Contains(libId, strictQuery.TopParentIds);

        // Unrestricted user: the filter is not applied, so the bypass stays unset
        // (Jellyfin evaluates it only inside its TopParentIds branch).
        var unrestrictedQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicArtist }
        };
        LibraryFilter.ApplyLibraryFilter(unrestrictedQuery, new Entities.User { AllowedLibraryIds = null }, _libraryManagerMock.Object);
        Assert.False(unrestrictedQuery.IncludeItemsByName == true);
        Assert.Empty(unrestrictedQuery.TopParentIds);
    }

    [Fact]
    public void ApplyLibraryFilter_PreResolvedScope_MirrorsBypassAndExemption()
    {
        // The pre-resolved overload (the hoisted-scope shape) must behave exactly
        // like the user-resolving one: bypass for MusicArtist, exemption skip for
        // an all-out-of-library kind set (code-review round 2 item 8).
        var libId = Guid.NewGuid();

        var artistQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicArtist }
        };
        LibraryFilter.ApplyLibraryFilter(artistQuery, new[] { libId });
        Assert.True(artistQuery.IncludeItemsByName);
        Assert.Contains(libId, artistQuery.TopParentIds);

        var artistOptOut = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicArtist }
        };
        LibraryFilter.ApplyLibraryFilter(artistOptOut, new[] { libId }, includeItemsByName: false);
        Assert.False(artistOptOut.IncludeItemsByName == true);
        Assert.Contains(libId, artistOptOut.TopParentIds);

        var playlistQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist }
        };
        LibraryFilter.ApplyLibraryFilter(playlistQuery, new[] { libId });
        Assert.Empty(playlistQuery.TopParentIds);

        // Null scope (unrestricted): nothing applied.
        var nullScopeQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.MusicArtist }
        };
        LibraryFilter.ApplyLibraryFilter(nullScopeQuery, null);
        Assert.Empty(nullScopeQuery.TopParentIds);
        Assert.False(nullScopeQuery.IncludeItemsByName == true);
    }

    [Fact]
    public void IsOutOfLibraryKind_MatchesExactlyTheExemptKinds()
    {
        Assert.True(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.Playlist));
        Assert.True(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel));
        Assert.False(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.Audio));
        Assert.False(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.MusicAlbum));
        Assert.False(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.MusicArtist));
        Assert.False(LibraryFilter.IsOutOfLibraryKind(Jellyfin.Data.Enums.BaseItemKind.Movie));
    }

    // --- JF-456: fused ResolveForUser ---

    [Fact]
    public void ResolveForUser_ReturnsNull_WhenUnrestricted()
    {
        var result = LibraryFilter.ResolveForUser(
            new Entities.User { AllowedLibraryIds = null }, _libraryManagerMock.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveForUser_ResolvesScope_WhenRestricted()
    {
        var libId = Guid.NewGuid();
        var result = LibraryFilter.ResolveForUser(
            new Entities.User { AllowedLibraryIds = new List<string> { libId.ToString() } },
            _libraryManagerMock.Object);

        Assert.NotNull(result);
        Assert.Contains(libId, result);
    }

    private class TestLibraryFilterHandler : BaseHandler
    {
        public TestLibraryFilterHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public void TestApplyLibraryFilter(InternalItemsQuery query, Entities.User user, ILibraryManager libraryManager)
            => ApplyLibraryFilter(query, user, libraryManager);
    }
}

/// <summary>
/// Tests for LibraryFilter.ResolveTopParentIds, the CollectionFolder to physical
/// folder resolution that is critical for per-user library filtering to work correctly.
/// Since JF-455 the result is the UNION of resolved physical folder IDs and the original
/// CollectionFolder IDs: the plugin's in-memory index maps are keyed by physical IDs
/// while Jellyfin's DB accepts both id spaces for TopParentIds.
/// </summary>
public class ResolveTopParentIdsTests
{
    [Fact]
    public void EmptyArray_ReturnsEmptyArray()
    {
        var lm = new Mock<ILibraryManager>().Object;
        var result = LibraryFilter.ResolveTopParentIds(Array.Empty<Guid>(), lm);
        Assert.Empty(result);
    }

    [Fact]
    public void NonCollectionFolder_PassThrough()
    {
        var folderId = Guid.NewGuid();
        var lmMock = new Mock<ILibraryManager>();
        // GetItemById returns a regular Folder (not CollectionFolder)
        var regularFolder = new Folder { Id = folderId };
        lmMock.Setup(lm => lm.GetItemById(folderId)).Returns(regularFolder);

        var result = LibraryFilter.ResolveTopParentIds(new[] { folderId }, lmMock.Object);

        Assert.Single(result);
        Assert.Equal(folderId, result[0]);
    }

    [Fact]
    public void CollectionFolder_ResolvesPhysicalFolderIds()
    {
        var cfId = Guid.Parse("bdf38141c3a366eb1a2a8240d2e65e68");
        var physicalId = Guid.Parse("da2977747a5ebb85bf22ba6cebbd70ea");

        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/video/cartoni" };

        var physicalFolder = new Folder { Id = physicalId };

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId)).Returns(cf);
        lmMock.Setup(lm => lm.FindByPath("/data/media/video/cartoni", true)).Returns(physicalFolder);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId }, lmMock.Object);

        // JF-455: physical folder ID + the original CollectionFolder ID (union)
        Assert.Equal(2, result.Length);
        Assert.Contains(physicalId, result);
        Assert.Contains(cfId, result);
    }

    [Fact]
    public void CollectionFolder_MultiplePhysicalLocations_ResolvesAll()
    {
        var cfId = Guid.Parse("a656b907eb3a73532e40e44b968d0225");
        var physId1 = Guid.NewGuid();
        var physId2 = Guid.NewGuid();

        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/video/serie", "/data/sonar" };

        var folder1 = new Folder { Id = physId1 };
        var folder2 = new Folder { Id = physId2 };

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId)).Returns(cf);
        lmMock.Setup(lm => lm.FindByPath("/data/media/video/serie", true)).Returns(folder1);
        lmMock.Setup(lm => lm.FindByPath("/data/sonar", true)).Returns(folder2);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId }, lmMock.Object);

        // Both physical folder IDs plus the CollectionFolder ID from the union
        Assert.Equal(3, result.Length);
        Assert.Contains(physId1, result);
        Assert.Contains(physId2, result);
        Assert.Contains(cfId, result);
    }

    [Fact]
    public void CollectionFolder_FindByPathReturnsNull_FallsBackToCollectionFolderId()
    {
        var cfId = Guid.NewGuid();
        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/nonexistent/path" };

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId)).Returns(cf);
        lmMock.Setup(lm => lm.FindByPath("/nonexistent/path", true)).Returns((BaseItem?)null);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId }, lmMock.Object);

        // When FindByPath fails, the fallback adds the CollectionFolder ID
        Assert.Single(result);
        Assert.Equal(cfId, result[0]);
    }

    [Fact]
    public void CollectionFolder_EmptyPhysicalLocations_FallsBackToCollectionFolderId()
    {
        var cfId = Guid.NewGuid();
        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = Array.Empty<string>();

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId)).Returns(cf);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId }, lmMock.Object);

        Assert.Single(result);
        Assert.Equal(cfId, result[0]);
    }

    [Fact]
    public void GetItemByIdReturnsNull_PassThrough()
    {
        var someId = Guid.NewGuid();
        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(someId)).Returns((BaseItem?)null);

        var result = LibraryFilter.ResolveTopParentIds(new[] { someId }, lmMock.Object);

        Assert.Single(result);
        Assert.Equal(someId, result[0]);
    }

    [Fact]
    public void MixedCollectionFolderAndPhysical_ResolvesCorrectly()
    {
        var cfId = Guid.NewGuid();
        var physicalId = Guid.NewGuid();
        var directFolderId = Guid.NewGuid();

        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };
        var physicalFolder = new Folder { Id = physicalId };
        var directFolder = new Folder { Id = directFolderId };

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId)).Returns(cf);
        lmMock.Setup(lm => lm.FindByPath("/data/media/music", true)).Returns(physicalFolder);
        lmMock.Setup(lm => lm.GetItemById(directFolderId)).Returns(directFolder);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId, directFolderId }, lmMock.Object);

        // Physical resolution + direct passthrough + the CollectionFolder ID from the union
        Assert.Equal(3, result.Length);
        Assert.Contains(physicalId, result); // resolved from CollectionFolder
        Assert.Contains(directFolderId, result); // passed through directly
        Assert.Contains(cfId, result); // union keeps the original CollectionFolder ID
    }

    [Fact]
    public void MultipleCollectionFolders_SecondWithEmptyLocations_StillFallsBack()
    {
        var cfId1 = Guid.NewGuid();
        var cfId2 = Guid.NewGuid();
        var physId1 = Guid.NewGuid();

        var cf1 = new CollectionFolder { Id = cfId1 };
        cf1.PhysicalLocationsList = new[] { "/data/media/music" };
        var cf2 = new CollectionFolder { Id = cfId2 };
        cf2.PhysicalLocationsList = Array.Empty<string>();

        var physicalFolder = new Folder { Id = physId1 };

        var lmMock = new Mock<ILibraryManager>();
        lmMock.Setup(lm => lm.GetItemById(cfId1)).Returns(cf1);
        lmMock.Setup(lm => lm.GetItemById(cfId2)).Returns(cf2);
        lmMock.Setup(lm => lm.FindByPath("/data/media/music", true)).Returns(physicalFolder);

        var result = LibraryFilter.ResolveTopParentIds(new[] { cfId1, cfId2 }, lmMock.Object);

        // Physical resolution + fallback id + both CollectionFolder IDs from the union
        Assert.Equal(3, result.Length);
        Assert.Contains(physId1, result); // resolved from first CollectionFolder
        Assert.Contains(cfId2, result); // fallback for second with empty locations
        Assert.Contains(cfId1, result); // union keeps the first CollectionFolder ID too
    }
}

/// <summary>
/// Tests that queue management handlers respect the QueueManagementEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class QueueManagementFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public QueueManagementFeatureFlagTests()
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
            Plugin.Instance.Configuration.QueueManagementEnabled = _config.QueueManagementEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-queue-feature-test-" + Guid.NewGuid());
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

    private async Task AssertDisabledWhenFlagOff(BaseHandler handler, string intentName)
    {
        _config.QueueManagementEnabled = false;
        Plugin.Instance!.Configuration.QueueManagementEnabled = false;

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = intentName } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddToQueue_ReturnsDisabledMessage_WhenQueueManagementDisabled()
    {
        var handler = new AddToQueueIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        await AssertDisabledWhenFlagOff(handler, "AddToQueueIntent");
    }

    [Fact]
    public async Task ClearQueue_ReturnsDisabledMessage_WhenQueueManagementDisabled()
    {
        var handler = new ClearQueueIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);
        await AssertDisabledWhenFlagOff(handler, "ClearQueueIntent");
    }

    [Fact]
    public async Task PlayNext_ReturnsDisabledMessage_WhenQueueManagementDisabled()
    {
        var handler = new PlayNextIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        await AssertDisabledWhenFlagOff(handler, "PlayNextIntent");
    }

    [Fact]
    public async Task ListQueue_ReturnsDisabledMessage_WhenQueueManagementDisabled()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _loggerFactory);
        await AssertDisabledWhenFlagOff(handler, "ListQueueIntent");
    }

    [Fact]
    public async Task ClearQueue_ProceedsNormally_WhenQueueManagementEnabled()
    {
        // QueueManagementEnabled defaults to true
        var handler = new ClearQueueIntentHandler(
            _sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // With queue management enabled but no active queue, should get the "queue empty" message,
        // NOT the feature disabled message.
        Assert.DoesNotContain("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListQueue_ProceedsNormally_WhenQueueManagementEnabled()
    {
        // QueueManagementEnabled defaults to true
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // With queue management enabled but no active queue, should get the "queue empty" message,
        // NOT the feature disabled message.
        Assert.DoesNotContain("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }
}
