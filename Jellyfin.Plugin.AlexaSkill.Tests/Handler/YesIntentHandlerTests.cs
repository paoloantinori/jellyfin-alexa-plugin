using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Assertions;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class YesIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public YesIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userManagerMock
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "yes-intent-tests");
    }

    private sealed class RecordingYesHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory)
        : YesIntentHandler(sessionManager, config, libraryManager, userManager, loggerFactory)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingYesHandler CreateHandler()
    {
        return new RecordingYesHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateYesIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.YesIntent" }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private Dictionary<string, object> CreateDisambiguationAttrs(
        List<DisambiguationHelper.MatchInfo> matches,
        int index,
        string type)
    {
        return new Dictionary<string, object>
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(matches),
            ["disambig_index"] = index,
            ["disambig_type"] = type
        };
    }

    [Fact]
    public void CanHandle_YesIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.YesIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoSessionAttributes_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithSessionAttributes_NoDisambiguationState_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var emptyAttrs = new Dictionary<string, object>();

        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            emptyAttrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_SongType_ReturnsAudioPlay()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns(song);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_PodcastTypeSeries_PlaysNewestEpisode()
    {
        // JF-599: the podcast confirm must resolve the newest Episode descendant of
        // a confirmed Series (AncestorIds, not ParentId) and play it; before the fix
        // this arm fell to MediaNotFound and the multi-match prompt dead-ended.
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "Generazione", Id = seriesId };
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Name = "Ep 12", Id = episodeId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(seriesId))
            .Returns(series);
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == Jellyfin.Data.Enums.BaseItemKind.Episode))))
            .Returns(new List<BaseItem> { episode });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = seriesId.ToString(), Name = "Generazione" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "podcast");

        var handler = CreateHandler();
        var session = CreateSession();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(episodeId, session.FullNowPlayingItem.Id);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_VideoTypeEpisodeOnScreenlessDevice_DegradesToAudioPlayer()
    {
        // JF-587: a confirmed EPISODE on a screenless context (the Echo Dot shape)
        // takes the audio-only AudioPlayer launch, not the screen-required refusal.
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ep. Pilot",
            episodeId,
            TestHelpers.TestStream(MediaBrowser.Model.Entities.MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaBrowser.Model.Entities.MediaStreamType.Audio, "aac"));

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(episodeId))
            .Returns(episode);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = episodeId.ToString(), Name = "Ep. Pilot" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            TestHelpers.CreateScreenlessContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var play = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_VideoType_ReturnsVideoDirective()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "Test Movie", Id = videoId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(videoId))
            .Returns(movie);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = videoId.ToString(), Name = "Test Movie" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
        // VideoApp.Launch must NOT include shouldEndSession
        Assert.Null(response.Response.ShouldEndSession);
        // JF-349: the disambiguation-confirmed video launch announces the title (was
        // silent). JF-501: the announce rides the progressive-response vehicle, so the
        // final launch response carries the directive ONLY.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Test Movie"), "progressive announce must speak the title");
    }

    /// <summary>
    /// JF-501: with the announce toggle OFF the confirmed video launch must keep today's
    /// silent shape: no progressive announce and no OutputSpeech on the final response.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithDisambiguationState_VideoType_AnnounceOff_NoProgressiveAnnounce()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "Test Movie", Id = videoId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(videoId))
            .Returns(movie);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = videoId.ToString(), Name = "Test Movie" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var user = TestHelpers.CreateTestUser();
        user.AnnounceNowPlaying = false;

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            user,
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.False(handler.Progressive.Contains("Test Movie"), "announce off must not send a progressive announce");
    }

    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_AudioBookItem_DoesNotReturnNoSongsInAlbum()
    {
        // JF-361: PlayBook disambiguation uses MediaTypeAlbum label for audiobooks. When the user
        // confirms, YesIntentHandler routes to PlayAlbum. A single-file AudioBook (no children)
        // must be treated as its own single track, not return "Non ci sono canzoni nell'album".
        var bookId = Guid.NewGuid();
        var book = new AudioBook { Name = "Test Book", Id = bookId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(bookId))
            .Returns(book);
        // Single-file audiobook: no child tracks, item itself is the audio.
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Test Book" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Must produce an AudioPlayer.Play (the item is playable audio), not a "NoSongsInAlbum" tell.
        Assert.NotNull(response.Response.Directives);
        Assert.True(response.Response.Directives.Count > 0 && response.Response.Directives[0] is AudioPlayerPlayDirective);
    }

    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_AudioBookItem_NativeControlsForBooks_UsesVideoAppLaunch()
    {
        // JF-361: when NativeControlsForBooks is on, the PlayBook path in YesIntentHandler must
        // route to VideoApp.Launch (seek bar), not AudioPlayer.Play.
        var bookId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var book = new AudioBook { Name = "Test Book", Id = bookId, ParentId = parentId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(bookId))
            .Returns(book);
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { book });

        // Enable NativeControlsForBooks (Plugin.Instance is set by EnsurePluginInstance in ctor)
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        Plugin.Instance.Configuration.ServerAddress = "http://localhost:8096";

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Test Book" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Restore defaults
        Plugin.Instance.Configuration.NativeControlsForBooks = false;

        Assert.NotNull(response.Response?.Directives);
        Assert.True(response.Response.Directives.Count > 0, $"Expected directives, got {response.Response.Directives?.Count ?? -1}. OutputSpeech: {response.Response.OutputSpeech}");
        Assert.IsType<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
            response.Response.Directives[0]);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_AlbumType_ReturnsAudioPlay()
    {
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var album = new MusicAlbum { Name = "Test Album", Id = albumId };
        var song = new Audio { Name = "Track 1", Id = songId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(albumId))
            .Returns(album);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = albumId.ToString(), Name = "Test Album" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_InvalidIndex_ReturnsUnexpectedResponse()
    {
        var songId = Guid.NewGuid();
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        // Index 5 is out of bounds for a list of 1 item
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 5, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ItemNotFound_ReturnsMediaNotFound()
    {
        var songId = Guid.NewGuid();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns((BaseItem?)null);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Missing Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("could not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SongType_SetsSessionQueue()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };
        var session = CreateSession();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns(song);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            attrs,
            CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(songId, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_NegativeIndex_ReturnsUnexpectedResponse()
    {
        var songId = Guid.NewGuid();
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, -1, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_VideoType_DirectiveContainsTitle()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "The Matrix", Id = videoId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(videoId))
            .Returns(movie);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = videoId.ToString(), Name = "The Matrix" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var directive = response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public async Task HandleAsync_AlbumType_AudioBookItem_UsesMediaTypesForChapters()
    {
        // Regression guard (JF-339 /code-review): PlayBook disambiguation reuses the "album"
        // type for audiobooks, so PlayAlbum receives an AudioBook. It must filter by
        // MediaTypes=Audio (covers AudioBook chapters); IncludeItemTypes=Audio would drop them
        // (chapters are BaseItemKind.AudioBook) and speak NoSongsInAlbum.
        var bookId = Guid.NewGuid();
        var chapter = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var book = new AudioBook { Name = "Test Audiobook", Id = bookId };

        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(book);

        InternalItemsQuery? captured = null;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q => q.MediaTypes != null)))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { chapter });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Test Audiobook" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "PlayAlbum must use MediaTypes=Audio for the album/audiobook path, not IncludeItemTypes — AudioBook chapters are BaseItemKind.AudioBook and would be dropped.");
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    // ========== JF-767 Finding A: the confirmation paths route through the ONE builders ==========

    /// <summary>
    /// JF-767 Finding A pin: a confirmed MUSIC album's whole-album enumeration routes
    /// through the ONE album-tracks builder (unpaged, session-user form) under the
    /// JF-666 library scope, WITH the JF-338 AlbumIds retry, the same triple the
    /// paged head (AlbumPlayService) runs, so a confirm answers exactly what the
    /// direct ask plays: split albums PLAY instead of answering NoSongsInAlbum, and
    /// the queue rows feed the JF-625 concat timeline the same field set the endpoint
    /// encodes (IncludeItemTypes=Audio, not the old hand-kept MediaTypes drift pair).
    /// Split-album server shape (empty ParentId pages, populated AlbumIds pages) so
    /// BOTH arms issue. RED on the pre-JF-767 tree (verified): no retry issues
    /// (captured == 1), the folder arm carries MediaTypes with no IncludeItemTypes,
    /// and neither arm carries TopParentIds.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumType_MusicAlbumConfirm_SplitAlbum_RoutesThroughBuilderScopeAndRetry()
    {
        var albumId = Guid.NewGuid();
        var musicLib = Guid.NewGuid();
        var album = new MusicAlbum { Name = "Split Album", Id = albumId };
        var song = new Audio { Name = "Tag-Linked Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(lm => lm.GetItemById(albumId)).Returns(album);

        var captured = new List<InternalItemsQuery>();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.AlbumIds != null && q.AlbumIds.Contains(albumId)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>());

        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { musicLib.ToString() });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = albumId.ToString(), Name = "Split Album" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            user,
            CreateSession(),
            attrs,
            CancellationToken.None);

        // The JF-338 retry issued: folder arm (empty) then the AlbumIds arm (populated).
        Assert.Equal(2, captured.Count);

        // Folder arm: the builder's field set (kind, order, scope).
        var folderArm = captured[0];
        Assert.True(folderArm.Recursive);
        Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, folderArm.IncludeItemTypes);
        Assert.Equal(QueueContinuationFetcher.AlbumTrackOrder, folderArm.OrderBy);
        Assert.Equal(albumId, folderArm.ParentId);
        Assert.NotNull(folderArm.User);
        Assert.Contains(musicLib, folderArm.TopParentIds);

        // AlbumIds arm (the retry): same scope, membership scoping field.
        var membershipArm = captured[1];
        Assert.Equal(new[] { albumId }, membershipArm.AlbumIds);
        Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, membershipArm.IncludeItemTypes);
        Assert.Contains(musicLib, membershipArm.TopParentIds);

        // The split album PLAYS on confirm (the direct ask already did, JF-338).
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    /// <summary>
    /// The ternary's other leg (JF-767 Finding A, superseded by JF-672 code-review
    /// F1): a NON-MusicAlbum parent confirmed under the "album" label routes through
    /// the ONE chapters builder's scoped unpaged sibling. The JF-361 kind discipline
    /// is the chapters core's MediaTypes=Audio axis (AudioBook chapter children are
    /// BaseItemKind.AudioBook, which the ALBUM builder's IncludeItemTypes=Audio
    /// would drop). The pre-JF-672 local initializer carried AlbumTrackOrder (the
    /// disc/track composite the chapters probe refuted), so the confirm leg answered
    /// a differently-ordered book than the direct PlayBook ask: RED on that tree
    /// (captured OrderBy was AlbumTrackOrder), and the pin now asserts the shared
    /// chapter order.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumType_NonMusicAlbumParent_RoutesThroughChaptersCore()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder { Name = "Chapter Folder", Id = folderId };
        var chapter = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(lm => lm.GetItemById(folderId)).Returns(folder);

        InternalItemsQuery? captured = null;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { chapter });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = folderId.ToString(), Name = "Chapter Folder" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(new[] { Jellyfin.Data.Enums.MediaType.Audio }, captured!.MediaTypes);
        Assert.True(captured.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "the non-MusicAlbum leg must keep MediaTypes=Audio (JF-361: AudioBook chapters are BaseItemKind.AudioBook)");
        // The shared chapter order, NOT AlbumTrackOrder (JF-672).
        Assert.Equal(QueueContinuationFetcher.AudiobookChapterOrder, captured.OrderBy);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    /// <summary>
    /// JF-767 Finding A pin (the PlayBook twin): a confirmed AudioBook resolves its
    /// chapters through the ONE audiobook chapters query
    /// (<see cref="QueueContinuationFetcher.BuildAudiobookChaptersQuery"/>) under the
    /// JF-666 library scope, the identical field set the hand-kept initializer
    /// carried (modulo the now-explicit StartIndex=0) plus the scope filter the
    /// tail's FetchAudiobookChapters already runs. RED on the pre-JF-767 tree
    /// (verified): the captured query carries no TopParentIds and no StartIndex.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumType_AudioBookConfirm_ChaptersQueryRoutesThroughBuilderWithScope()
    {
        var bookId = Guid.NewGuid();
        var bookLib = Guid.NewGuid();
        var book = new AudioBook { Name = "Scoped Book", Id = bookId };
        var chapter = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(book);

        InternalItemsQuery? captured = null;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { chapter });

        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { bookLib.ToString() });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Scoped Book" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            user,
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(captured);
        // The builder's field set (MediaTypes discipline is deliberate for chapters).
        Assert.Equal(new[] { Jellyfin.Data.Enums.MediaType.Audio }, captured!.MediaTypes);
        Assert.Equal(bookId, captured.ParentId);
        Assert.True(captured.Recursive);
        Assert.Equal(0, captured.StartIndex);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), captured.Limit);
        // The JF-666 scope the tail runs under (FetchAudiobookChapters parity).
        Assert.Contains(bookLib, captured.TopParentIds);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    /// <summary>
    /// JF-793 Finding 1 RED PROOF: PlayBook's disambiguation stores CHAPTER leaves
    /// (the JF-791 shape: Jellyfin never types a multi-file book folder as AudioBook,
    /// so the book search returns chapter leaves), so the confirmed item reaching the
    /// YesIntent PlayBook leg is a leaf. The pre-fix leg ran the chapters query on the
    /// leaf's OWN Id, enumerated zero children, and the single-file fallback played the
    /// ONE confirmed chapter then silence: the exact one-chapter-then-silence defect
    /// the direct ask lost in JF-791, on the confirm path, violating the
    /// confirm-must-match-ask rule (the parallel-dispatch invariant). The leg must
    /// climb the confirmed leaf to the book folder the same way the head path does.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_AudioBookChapterLeaf_ConfirmClimbsToBookFolder()
    {
        var bookFolderId = Guid.NewGuid();
        var chapterLeaf = new AudioBook
        {
            Name = "Measure What Matters - Chapter 22",
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = "/audiobooks/measure-what-matters/ch22.mp3"
        };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(chapterLeaf.Id))
            .Returns(chapterLeaf);
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId });

        // 26 chapters with the initial page of 5: the confirm's chapters query must
        // run on the FOLDER id and see the page; any other parent (the pre-fix leaf
        // id) enumerates nothing, the real server's answer for a leaf.
        List<BaseItem> chapters = Enumerable.Range(1, 26)
            .Select(i => (BaseItem)new Audio
            {
                Name = $"Measure What Matters - Chapter {i:00}",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId
            })
            .ToList();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == bookFolderId
                ? chapters.Take(ProgressiveQueueConstants.GetInitialFetchSize()).ToList()
                : new List<BaseItem>());

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = chapterLeaf.Id.ToString(), Name = chapterLeaf.Name };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var session = CreateSession();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            attrs,
            CancellationToken.None);

        // The confirm plays the BOOK's first chapter, not the confirmed leaf alone:
        // the queue carries the page and the directive launches chapter 1.
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
        Assert.Equal(chapters[0].Id, session.FullNowPlayingItem!.Id);
    }

    [Fact]
    public async Task HandleAsync_ArtistType_SongQuery_UsesIncludeItemTypesNotMediaTypes()
    {
        // JF-667 (JF-358 sibling): the confirmed-artist songs query is ArtistIds-filtered,
        // so it must filter via IncludeItemTypes=Audio; MediaTypes does not constrain an
        // ArtistIds query (entire audio library on 10.11.x, zero rows at offset on 12.x).
        var artistId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Test Artist", Id = artistId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(artistId))
            .Returns(artist);

        InternalItemsQuery? captured = null;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Song", Id = Guid.NewGuid() } });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = artistId.ToString(), Name = "Test Artist" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, DisambiguationHelper.MediaTypeArtist);

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains(artistId, captured!.ArtistIds);
        Assert.NotNull(captured.IncludeItemTypes);
        Assert.Contains(Jellyfin.Data.Enums.BaseItemKind.Audio, captured.IncludeItemTypes);
        TestHelpers.AssertNoMediaTypesFilter(captured!, "artist query");
        // JF-690 review: the confirm leg orders by popularity like every direct
        // play path, so "yes" starts the artist on their most popular track.
        Assert.NotEmpty(captured.OrderBy);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    // ========== JF-507: codec-gated audio-launch URL on the resume-yes path ==========

    private Dictionary<string, object> CreateResumeAttrs(Guid itemId, int offsetMs)
    {
        var resumeState = new ResumeHelper.ResumeState { ItemId = itemId.ToString(), OffsetMs = offsetMs };
        return new Dictionary<string, object>
        {
            ["resume_state"] = JsonConvert.SerializeObject(resumeState)
        };
    }

    /// <summary>
    /// The incident shape (2026-09-06 corr=f0240020): accepting the resume offer for an
    /// EAC3 episode on a screenless device must NOT build the raw static /Audio/ URL
    /// (the Dot played 1ms and died); it routes to the audio-only episode HLS transcode
    /// with the offset baked into the URL and directive offset 0.
    /// </summary>
    [Fact]
    public async Task ResumeConfirmation_Eac3Episode_LaunchesAudioOnlyHlsTranscode()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

        _libraryManagerMock.Setup(lm => lm.GetItemById(id)).Returns(episode);

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CreateResumeAttrs(id, 300000),
            CancellationToken.None);

        var directive = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("/Audio/", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// The compatible shape (h264 + aac) keeps the raw static /Audio/ URL and the
    /// directive offset: zero change for sources that already play.
    /// </summary>
    [Fact]
    public async Task ResumeConfirmation_AacEpisode_KeepsStaticAudioUrlAndOffset()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "FreeCommerce",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));

        _libraryManagerMock.Setup(lm => lm.GetItemById(id)).Returns(episode);

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CreateResumeAttrs(id, 300000),
            CancellationToken.None);

        var directive = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains($"/Audio/{id}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("video-audio", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(300000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }
}
