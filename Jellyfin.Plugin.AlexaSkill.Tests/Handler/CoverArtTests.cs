using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// A minimal concrete handler used to test the <see cref="BaseHandler.BuildAudioPlayerResponse"/> method directly.
/// </summary>
internal class CoverArtTestHandler : BaseHandler
{
    public CoverArtTestHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Empty());
}

[Collection("Plugin")]
public class CoverArtTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public CoverArtTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private CoverArtTestHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private static Entities.User CreateUser(Guid? id = null, string token = "test-token")
        => TestHelpers.CreateTestUser(id, jellyfinToken: token);

    private static Audio CreateSong(string name = "Test Song", Guid? id = null)
        => new() { Name = name, Id = id ?? Guid.NewGuid() };

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_IncludesCoverArt()
    {
        var handler = CreateHandler();
        var song = CreateSong("My Song");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.NotNull(directive.AudioItem.Metadata);
        Assert.Equal("My Song", directive.AudioItem.Metadata.Title);

        Assert.NotNull(directive.AudioItem.Metadata.Art);
        Assert.NotEmpty(directive.AudioItem.Metadata.Art.Sources);
        Assert.NotNull(directive.AudioItem.Metadata.BackgroundImage);
        Assert.NotEmpty(directive.AudioItem.Metadata.BackgroundImage.Sources);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_CorrectImageUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong(id: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var user = CreateUser(token: "my-api-key");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        string expectedImageUrl = "http://localhost:8096/Items/11111111-1111-1111-1111-111111111111/Images/Primary?api_key=my-api-key";
        Assert.Equal(expectedImageUrl, directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Equal(expectedImageUrl, directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_SetsStreamUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser(token: "stream-token");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(streamUrl, directive.AudioItem.Stream.Url);
        Assert.Equal(itemId, directive.AudioItem.Stream.Token);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithOffset_IncludesOffset()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user, offsetInMilliseconds: 30000);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(30000, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithZeroOffset_DefaultsToZero()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NullItem_EmptyMetadataUrls()
    {
        var handler = CreateHandler();
        var user = CreateUser(token: "null-test-token");
        string itemId = Guid.NewGuid().ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, null!, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.NotNull(directive.AudioItem.Metadata);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.Title);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesReplaceAllPlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesEnqueuePlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.Enqueue, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_EndsSession()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_VersionIsSet()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.Equal("1.0", response.Version);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_SingleDirective()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ArtAndBackgroundImageUseSameUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(
            directive.AudioItem.Metadata.Art.Sources[0].Url,
            directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ImageUrlContainsItemId()
    {
        var handler = CreateHandler();
        var songId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var song = CreateSong("Song", songId);
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Contains(songId.ToString(), directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Contains("Images/Primary", directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ImageUrlContainsApiKey()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser(token: "secret-key-123");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Contains("api_key=secret-key-123", directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_NameWithSpecialCharacters()
    {
        var handler = CreateHandler();
        var song = CreateSong("Rock & Roll - Live (Remastered)");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Rock & Roll - Live (Remastered)", directive.AudioItem.Metadata.Title);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesReplaceEnqueuedPlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceEnqueued, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.ReplaceEnqueued, directive.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_AudioWithArtist_SubtitleShowsArtist()
    {
        var handler = CreateHandler();
        var song = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "Queen" },
            Album = "A Night at the Opera"
        };
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Queen", directive.AudioItem.Metadata.Subtitle);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_AudioWithoutArtist_SubtitleFallsBackToAlbum()
    {
        var handler = CreateHandler();
        var song = new Audio
        {
            Name = "Instrumental Track",
            Id = Guid.NewGuid(),
            Artists = new List<string>(),
            Album = "Greatest Hits"
        };
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Greatest Hits", directive.AudioItem.Metadata.Subtitle);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_AudioNoArtistNoAlbum_SubtitleIsEmpty()
    {
        var handler = CreateHandler();
        var song = new Audio
        {
            Name = "Unknown Track",
            Id = Guid.NewGuid()
        };
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(string.Empty, directive.AudioItem.Metadata.Subtitle);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_Episode_SubtitleShowsSeriesName()
    {
        var handler = CreateHandler();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Pilot",
            Id = Guid.NewGuid(),
            SeriesName = "Breaking Bad"
        };
        var user = CreateUser();
        string itemId = episode.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, episode, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Breaking Bad", directive.AudioItem.Metadata.Subtitle);
        Assert.True(response.Response.ShouldEndSession);
    }
}

/// <summary>
/// Tests for the VideoApp audio response path, activated when NativeControlsForAudio is enabled.
/// Verifies that the video-audio endpoint URL is used instead of the raw audio stream URL.
/// </summary>
[Collection("Plugin")]
public class VideoAppAudioTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public VideoAppAudioTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true };
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
            Plugin.Instance.Configuration.NativeControlsForAudio = true;
            Plugin.Instance.Configuration.ServerAddress = "http://localhost:8096/";
            return;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "alexa-videoapp-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(_config);

        var userManager = new Mock<IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096/";
        plugin.Configuration.NativeControlsForAudio = true;
    }

    private VideoAppTestHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private static Entities.User CreateUser(string token = "test-token")
        => TestHelpers.CreateTestUser(jellyfinToken: token);

    private static Audio CreateSong(string name = "Test Song", Guid? id = null)
        => new() { Name = name, Id = id ?? Guid.NewGuid() };

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_UsesVideoAppDirective()
    {
        var handler = CreateHandler();
        var song = CreateSong("My Song");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));
        Assert.NotNull(directive.VideoItem);
        // VideoApp responses MUST end the session — null/false breaks intent routing
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_SourceUsesVideoAudioEndpoint()
    {
        var handler = CreateHandler();
        var song = CreateSong(id: Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var user = CreateUser(token: "my-video-token");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Contains("/alexaskill/api/video-audio/", directive.VideoItem.Source);
        Assert.Contains(itemId, directive.VideoItem.Source);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_SourceDoesNotUseRawStreamUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));

        // Should NOT contain the raw /Audio/.../stream path
        Assert.DoesNotContain("/Audio/", directive.VideoItem.Source);
        Assert.DoesNotContain("/stream", directive.VideoItem.Source);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_EnqueueStaysAudioPlayer()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);
        var context = TestHelpers.CreateTestContext();

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.Enqueue, streamUrl, itemId, song, user, context);

        // Enqueue should stay as AudioPlayer, not VideoApp
        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_WithOffsetStaysAudioPlayer()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user, offsetInMilliseconds: 5000);

        // Resume with offset should stay as AudioPlayer
        Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_MetadataHasTitle()
    {
        var handler = CreateHandler();
        var song = CreateSong("Stairway to Heaven");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));

        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("Stairway to Heaven", directive.VideoItem.Metadata.Title);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_MetadataHasSubtitle()
    {
        var handler = CreateHandler();
        var song = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "Queen" }
        };
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Queen", directive.VideoItem.Metadata.Subtitle);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void GetVideoAudioUrl_BuildsCorrectUrl()
    {
        var handler = CreateHandler();
        string itemId = "33333333-3333-3333-3333-333333333333";

        string url = handler.TestGetVideoAudioUrl(itemId);

        Assert.Equal("http://localhost:8096/alexaskill/api/video-audio/33333333-3333-3333-3333-333333333333", url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_NullItem_EmptyTitle()
    {
        var handler = CreateHandler();
        var user = CreateUser();
        string itemId = Guid.NewGuid().ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, null!, user);

        var directive = Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(string.Empty, directive.VideoItem.Metadata.Title);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOn_ShouldEndSession()
    {
        // Regression test: VideoApp responses MUST use ShouldEndSession=true.
        // Using null/false keeps the session open, which breaks intent routing —
        // the Echo sends SessionEndedRequest instead of routing to the correct
        // intent handler (same issue as AudioPlayer with ShouldEndSession=false).
        var handler = CreateHandler();
        var song = CreateSong("Test Song");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.IsType<VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));
        Assert.True(response.Response.ShouldEndSession,
            "VideoApp responses must set ShouldEndSession=true to match the Movie " +
            "handler pattern and prevent session-based intent routing failures");
    }
}

/// <summary>
/// Test handler exposing BuildAudioPlayerResponse and GetVideoAudioUrl for VideoApp tests.
/// </summary>
internal class VideoAppTestHandler : BaseHandler
{
    public VideoAppTestHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Empty());

    public string TestGetVideoAudioUrl(string itemId)
        => GetVideoAudioUrl(itemId);
}
