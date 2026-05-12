using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for media type toggle defaults in PluginConfiguration.
/// </summary>
public class MediaTypeConfigurationDefaults
{
    [Fact]
    public void MediaTypeToggles_DefaultToTrue()
    {
        var config = new PluginConfiguration();

        Assert.True(config.MusicEnabled);
        Assert.True(config.VideosEnabled);
        Assert.True(config.BooksEnabled);
    }

    [Fact]
    public void MediaTypeToggles_CanBeSetToFalse()
    {
        var config = new PluginConfiguration
        {
            MusicEnabled = false,
            VideosEnabled = false,
            BooksEnabled = false
        };

        Assert.False(config.MusicEnabled);
        Assert.False(config.VideosEnabled);
        Assert.False(config.BooksEnabled);
    }
}

/// <summary>
/// Tests for BaseHandler.FilterByContentAccess static method.
/// </summary>
[Collection("Plugin")]
public class FilterByContentAccessTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public FilterByContentAccessTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
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
            Plugin.Instance.Configuration.MusicEnabled = _config.MusicEnabled;
            Plugin.Instance.Configuration.VideosEnabled = _config.VideosEnabled;
            Plugin.Instance.Configuration.BooksEnabled = _config.BooksEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-content-filter-test-" + Guid.NewGuid());
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
    public void Filter_ReturnsAll_WhenEverythingEnabled()
    {
        // All media type toggles default to true
        var types = new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.AudioBook };

        var result = TestContentHandler.TestFilter(types);

        Assert.Equal(3, result.Length);
        Assert.Contains(BaseItemKind.Audio, result);
        Assert.Contains(BaseItemKind.Movie, result);
        Assert.Contains(BaseItemKind.AudioBook, result);
    }

    [Fact]
    public void Filter_RemovesAudio_WhenMusicDisabled()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;

        var types = new[] { BaseItemKind.Audio, BaseItemKind.Movie };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Movie, result);
        Assert.DoesNotContain(BaseItemKind.Audio, result);
    }

    [Fact]
    public void Filter_RemovesVideo_WhenVideosDisabled()
    {
        _config.VideosEnabled = false;
        Plugin.Instance!.Configuration.VideosEnabled = false;

        var types = new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Audio, result);
        Assert.DoesNotContain(BaseItemKind.Movie, result);
        Assert.DoesNotContain(BaseItemKind.Episode, result);
    }

    [Fact]
    public void Filter_RemovesBooks_WhenBooksDisabled()
    {
        _config.BooksEnabled = false;
        Plugin.Instance!.Configuration.BooksEnabled = false;

        var types = new[] { BaseItemKind.AudioBook, BaseItemKind.Audio };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Audio, result);
        Assert.DoesNotContain(BaseItemKind.AudioBook, result);
    }

    [Fact]
    public void Filter_KeepsPlaylist_WhenOtherTypesDisabled()
    {
        _config.MusicEnabled = false;
        _config.VideosEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        Plugin.Instance!.Configuration.VideosEnabled = false;

        var types = new[] { BaseItemKind.Playlist, BaseItemKind.Audio };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Playlist, result);
        Assert.DoesNotContain(BaseItemKind.Audio, result);
    }

    [Fact]
    public void Filter_ReturnsSameArray_WhenAllPass()
    {
        // When everything is enabled, the original array reference is returned
        var types = new[] { BaseItemKind.Audio, BaseItemKind.Movie };

        var result = TestContentHandler.TestFilter(types);

        Assert.Same(types, result);
    }

    [Fact]
    public void Filter_RemovesMusicAlbum_WhenMusicDisabled()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;

        var types = new[] { BaseItemKind.MusicAlbum, BaseItemKind.Movie };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Movie, result);
    }

    [Fact]
    public void Filter_RemovesMusicArtist_WhenMusicDisabled()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;

        var types = new[] { BaseItemKind.MusicArtist, BaseItemKind.Movie };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Movie, result);
    }

    [Fact]
    public void Filter_RemovesSeries_WhenVideosDisabled()
    {
        _config.VideosEnabled = false;
        Plugin.Instance!.Configuration.VideosEnabled = false;

        var types = new[] { BaseItemKind.Series, BaseItemKind.Audio };

        var result = TestContentHandler.TestFilter(types);

        Assert.Single(result);
        Assert.Contains(BaseItemKind.Audio, result);
    }

    /// <summary>
    /// Minimal concrete handler to expose the protected static FilterByContentAccess for testing.
    /// </summary>
    private class TestContentHandler : BaseHandler
    {
        public TestContentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public static BaseItemKind[] TestFilter(BaseItemKind[] types) => FilterByContentAccess(types);
    }
}

/// <summary>
/// Tests for BaseHandler.IfMediaTypeDisabled instance method.
/// </summary>
[Collection("Plugin")]
public class IfMediaTypeDisabledTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestMediaTypeHandler _handler;

    public IfMediaTypeDisabledTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestMediaTypeHandler(_sessionManagerMock.Object, _config, _loggerFactory);
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
            Plugin.Instance.Configuration.MusicEnabled = _config.MusicEnabled;
            Plugin.Instance.Configuration.VideosEnabled = _config.VideosEnabled;
            Plugin.Instance.Configuration.BooksEnabled = _config.BooksEnabled;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-mediatype-test-" + Guid.NewGuid());
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
    public void ReturnsNull_WhenMusicEnabled()
    {
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.MusicEnabled, request);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenVideosEnabled()
    {
        var request = new IntentRequest { Intent = new Intent { Name = "PlayVideoIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.VideosEnabled, request);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenBooksEnabled()
    {
        var request = new IntentRequest { Intent = new Intent { Name = "PlayBookIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.BooksEnabled, request);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsResponse_WhenMusicDisabled()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.MusicEnabled, request);

        Assert.NotNull(result);
    }

    [Fact]
    public void ReturnsResponse_WhenVideosDisabled()
    {
        _config.VideosEnabled = false;
        Plugin.Instance!.Configuration.VideosEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "PlayVideoIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.VideosEnabled, request);

        Assert.NotNull(result);
    }

    [Fact]
    public void ReturnsResponse_WhenBooksDisabled()
    {
        _config.BooksEnabled = false;
        Plugin.Instance!.Configuration.BooksEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "PlayBookIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.BooksEnabled, request);

        Assert.NotNull(result);
    }

    [Fact]
    public void Response_ContainsNotAvailableMessage()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };

        var result = _handler.TestIfMediaTypeDisabled(c => c.MusicEnabled, request);

        Assert.NotNull(result);
        var text = TestHelpers.GetSpeechText(result);
        Assert.Contains("not available", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal concrete handler to expose the protected IfMediaTypeDisabled for testing.
    /// </summary>
    private class TestMediaTypeHandler : BaseHandler
    {
        public TestMediaTypeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public SkillResponse? TestIfMediaTypeDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
            => IfMediaTypeDisabled(isEnabled, request);
    }
}
