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
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class YesIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public YesIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
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
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null,
        IArtistIndex? artistIndex = null)
        : YesIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory, queueManager, artistIndex)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingYesHandler CreateHandler(DeviceQueueManager? queueManager = null, IArtistIndex? artistIndex = null)
    {
        return new RecordingYesHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            queueManager,
            artistIndex);
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
        // Single-file audiobook: no child tracks, item itself is the audio. The
        // confirm rides the head's page executor since JF-795, so the empty page
        // must come back through GetItemsResult.
        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

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
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { book }, TotalRecordCount = 1 });

        // Enable NativeControlsForBooks (Plugin.Instance is set by EnsurePluginInstance in ctor).
        // JF-795 code-review F2: restore in a finally, so an assertion failure cannot
        // leak the flag into the [Collection("Plugin")] siblings.
        bool originalNativeControlsForBooks = Plugin.Instance!.Configuration.NativeControlsForBooks;
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        Plugin.Instance.Configuration.ServerAddress = "http://localhost:8096";
        try
        {
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

            Assert.NotNull(response.Response?.Directives);
            Assert.True(response.Response.Directives.Count > 0, $"Expected directives, got {response.Response.Directives?.Count ?? -1}. OutputSpeech: {response.Response.OutputSpeech}");
            Assert.IsType<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                response.Response.Directives[0]);
        }
        finally
        {
            // Restore the CAPTURED pre-test value (gate-marker tail F3): a literal
            // false here would silently disable the flag for a Plugin-collection
            // sibling that legitimately enabled it at fixture setup.
            Plugin.Instance.Configuration.NativeControlsForBooks = originalNativeControlsForBooks;
        }
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

        // JF-805: the confirm rides the ONE album play flow, whose track page
        // executor is SafeGetItemsResult (the paged GetItemsResult form).
        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { song }, TotalRecordCount = 1 });

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

        var directive = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Equal(songId.ToString(), directive.AudioItem.Stream.Token);
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
            .Setup(lm => lm.GetItemsResult(It.Is<InternalItemsQuery>(q => q.MediaTypes != null)))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { chapter }, TotalRecordCount = 1 });

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
    /// JF-767 Finding A pin (JF-805 evolution): a confirmed MUSIC album's track
    /// enumeration routes through the ONE album play flow
    /// (AlbumPlayService.BuildAlbumPlayResponseAsync), so the confirm issues the
    /// same PAGED queries under the JF-666 library scope, WITH the JF-338
    /// AlbumIds retry, the same triple the paged head runs: a confirm answers
    /// exactly what the direct ask plays (split albums PLAY instead of
    /// answering NoSongsInAlbum) and the queue rows feed the JF-625 concat
    /// timeline the same field set the endpoint encodes
    /// (IncludeItemTypes=Audio, not the old hand-kept MediaTypes drift pair).
    /// Split-album server shape (empty ParentId pages, populated AlbumIds
    /// pages) so BOTH arms issue. RED on the pre-JF-805 tree (verified): the
    /// confirm issued two UNPAGED GetItemList queries instead (no
    /// StartIndex/Limit; the captured GetItemsResult list stayed empty).
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
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.AlbumIds != null && q.AlbumIds.Contains(albumId)
                    ? new QueryResult<BaseItem> { Items = new List<BaseItem> { song }, TotalRecordCount = 1 }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { musicLib.ToString() });

        var (response, _, _, _) = await ConfirmAlbumAsync(album, user);

        // The JF-338 retry issued: folder arm (empty) then the AlbumIds arm
        // (populated); the 1-track page is the whole album, so no deep
        // fetch follows.
        Assert.Equal(2, captured.Count);

        // Folder arm: the builder's field set (kind, order, page, scope).
        var folderArm = captured[0];
        Assert.True(folderArm.Recursive);
        Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, folderArm.IncludeItemTypes);
        Assert.Equal(QueueContinuationFetcher.AlbumTrackOrder, folderArm.OrderBy);
        Assert.Equal(albumId, folderArm.ParentId);
        Assert.Equal(0, folderArm.StartIndex);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), folderArm.Limit);
        Assert.NotNull(folderArm.User);
        Assert.Contains(musicLib, folderArm.TopParentIds);

        // AlbumIds arm (the retry): same scope and page, membership scoping field.
        var membershipArm = captured[1];
        Assert.Equal(new[] { albumId }, membershipArm.AlbumIds);
        Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, membershipArm.IncludeItemTypes);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), membershipArm.Limit);
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
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { chapter }, TotalRecordCount = 1 });

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
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { chapter }, TotalRecordCount = 1 });

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
    /// The shared confirmed-book fixture (the JF-793/JF-795 book-confirm tests):
    /// a "Measure What Matters" book folder served by GetItemById, an optional
    /// chapter-LEAF payload served the same way (the JF-791 shape), and the
    /// paging-honoring chapters executor the confirm rides since JF-795 (the
    /// GetItemsResult page plus the deep unpaged re-scan; any OTHER parent
    /// enumerates nothing, the real server's answer for a leaf, so a broken climb
    /// still fails the queue-count assertions).
    /// </summary>
    private (List<BaseItem> Chapters, Guid BookFolderId, AudioBook? LeafPayload) SetupConfirmedBook(int chapterCount = 26, bool leafPayload = false)
    {
        Guid bookFolderId = Guid.NewGuid();
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId, Path = "/audiobooks/measure-what-matters" });

        AudioBook? leaf = null;
        if (leafPayload)
        {
            leaf = new AudioBook
            {
                Name = "Measure What Matters - Chapter 22",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/measure-what-matters/ch22.mp3"
            };
            _libraryManagerMock
                .Setup(lm => lm.GetItemById(leaf.Id))
                .Returns(leaf);
        }

        List<BaseItem> chapters = Enumerable.Range(1, chapterCount)
            .Select(i => (BaseItem)new Audio
            {
                Name = $"Measure What Matters - Chapter {i:00}",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId
            })
            .ToList();

        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == bookFolderId
                ? new QueryResult<BaseItem>
                {
                    Items = chapters
                        .Skip(q.StartIndex ?? 0)
                        .Take(q.Limit ?? chapters.Count)
                        .ToList(),
                    TotalRecordCount = chapters.Count
                }
                : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        return (chapters, bookFolderId, leaf);
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
        (List<BaseItem> chapters, Guid _, AudioBook? leaf) = SetupConfirmedBook(leafPayload: true);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = leaf!.Id.ToString(), Name = leaf.Name };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();
        try
        {
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                context,
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
        finally
        {
            // JF-795: the confirm now mints the book's continuation like the ask;
            // clean the static store so the [Collection("Plugin")] siblings stay
            // isolated.
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    /// <summary>
    /// JF-793 Finding 2 twin pin (the confirm leg of the live shared-container shape):
    /// a confirmed COLLAPSED single-file book under the shared container (the minix
    /// census shape: the leaf's file sits one directory deeper than its ParentId
    /// container) must NOT climb: the container is not a book folder, and the
    /// confirmed book plays as its own single track (the JF-361 duality), never a
    /// container merge.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_CollapsedSingleFileBook_UnderSharedContainer_PlaysAsOwnTrack()
    {
        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Radical Candor",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
        };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(book.Id))
            .Returns(book);
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(containerId))
            .Returns(new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" });

        // The merge the discriminator must prevent: the container's enumeration
        // returns the sibling single-file books. JF-795: the confirm rides the
        // head's page executor (GetItemsResult).
        List<BaseItem> siblings = new()
        {
            book,
            new AudioBook
            {
                Name = "Managing Humans",
                Id = Guid.NewGuid(),
                ParentId = containerId,
                Path = "/audiobooks/Managing Humans/Managing Humans.m4b"
            }
        };
        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == containerId
                ? new QueryResult<BaseItem> { Items = siblings, TotalRecordCount = siblings.Count }
                : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = book.Id.ToString(), Name = book.Name };
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

        // The ONE confirmed book plays as its own track, not the container merge.
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(book.Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(book.Id, session.FullNowPlayingItem!.Id);
    }

    /// <summary>
    /// JF-793 code-review F1 RED PROOF: the finding-3 normalization emits the book
    /// FOLDER id as the confirm payload (pinned by
    /// PlayBook_MultiMatchDisambiguation_PresentsBookGranularChoices), but a plain
    /// Folder is not an AudioBook, so the pre-fix routing gate
    /// (<c>IsAudioBook(item)</c>) dropped these confirms into the PlayAlbum arm:
    /// the unpaged whole-book queue (26, not the PlayBook leg's paged 5), no
    /// resume, no device queue, and under NativeControlsForBooks a plain
    /// AudioPlayer chapter-1 launch where the direct ask gives the VideoApp concat.
    /// A "yes" on a multi-chapter book must reach the PlayBook leg, whatever
    /// payload shape the prompt minted (leaf id or folder id).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookFolderIdPayload_RoutesToPlayBookLeg()
    {
        (List<BaseItem> chapters, Guid bookFolderId, _) = SetupConfirmedBook();

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookFolderId.ToString(), Name = "Measure What Matters" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();
        try
        {
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                context,
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            // The PlayBook leg answers: the paged initial queue (5), chapter 1 first.
            // The pre-fix PlayAlbum arm enumerated the whole book unpaged (26).
            var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
                Assert.Single(response.Response.Directives!));
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
            Assert.Equal(chapters[0].Id, session.FullNowPlayingItem!.Id);
        }
        finally
        {
            // JF-795: the confirm now mints the book's continuation like the ask.
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // ========== JF-795: the confirm-must-match-ask axes (continuation + resume) ==========

    /// <summary>
    /// JF-795 RED PROOF (continuation axis): a confirmed multi-chapter book must
    /// mint the progressive QueueContinuation exactly like the direct ask. The
    /// pre-fix confirm leg queued ONLY the initial page (5) and never touched
    /// QueueContinuationStore, so a "yes" on a 26-chapter book truncated the book
    /// at 5 chapters while the direct ask played the whole book through the tail
    /// fetcher (the confirm-must-match-ask rule on the queue-completeness axis).
    /// Pin: continuation minted with SourceType Audiobook, ParentId the book
    /// folder, StartIndex 5 (page start 0 + page count 5), TotalCount 26.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookFolderConfirm_MintsQueueContinuation()
    {
        (List<BaseItem> chapters, Guid bookFolderId, _) = SetupConfirmedBook();

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookFolderId.ToString(), Name = "Measure What Matters" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();
        try
        {
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                context,
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            // The play itself launches chapter 1 fresh (no progress on the book).
            var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
                Assert.Single(response.Response.Directives!));
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);

            // THE AXIS: the continuation is minted (pre-fix: null, the book
            // truncated at the initial page).
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal("Audiobook", continuation!.SourceType);
            Assert.Equal(bookFolderId, continuation.ParentId);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), continuation.StartIndex);
            Assert.Equal(26, continuation.TotalCount);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    /// <summary>
    /// JF-795 RED PROOF (resume axis): a confirmed book with deep progress
    /// (chapter 22 of 26, beyond the initial page) must resume at the
    /// position-holding chapter exactly like the direct ask (the JF-793
    /// finding-4 shape). The pre-fix confirm leg never ran FindResumeTrackIndex
    /// at all, so the confirm answered a deep-progress book by restarting at
    /// chapter 1 at 0:00 (the confirm-must-match-ask rule on the resume axis).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookConfirm_DeepProgress_ResumesAtPositionHoldingChapter()
    {
        (List<BaseItem> chapters, Guid bookFolderId, _) = SetupConfirmedBook();

        // Deep progress: chapter 22 (index 21) in progress at 10 minutes.
        var inProgress = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(10).Ticks
        };
        _userDataManagerMock
            .Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                item.Id == chapters[21].Id ? inProgress : null);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookFolderId.ToString(), Name = "Measure What Matters" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();
        try
        {
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                context,
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            // THE AXIS: the position-holding chapter launches at its position,
            // not chapter 1 at 0:00.
            var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
                Assert.Single(response.Response.Directives!));
            Assert.Equal(chapters[21].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

            // The queue re-slices at the position-holding chapter (chapters 22-26);
            // nothing remains beyond it, so no continuation is minted.
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
            Assert.Equal(chapters[21].Id, session.FullNowPlayingItem!.Id);
            Assert.Equal(chapters[21].Id, session.NowPlayingQueue[0].Id);
            Assert.Equal(chapters[25].Id, session.NowPlayingQueue[4].Id);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    /// <summary>
    /// JF-795 /simplify gate pin (the JF-611 podcast-confirm shape): a book
    /// disambiguation prompt confirmed after an admin disabled books must answer
    /// the feature-disabled Tell, not launch the book through the confirm (the
    /// confirm-must-match-ask rule extends to the disabled answer the direct
    /// ask gives). The pre-JF-795 leg never gated either; the routing round
    /// adopted the podcast leg's gate.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookConfirm_BooksDisabled_AnswersFeatureDisabled()
    {
        (List<BaseItem> _, Guid bookFolderId, _) = SetupConfirmedBook();

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookFolderId.ToString(), Name = "Measure What Matters" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        bool originalBooksEnabled = Plugin.Instance!.Configuration.BooksEnabled;
        Plugin.Instance!.Configuration.BooksEnabled = false;
        try
        {
            var handler = CreateHandler();
            var session = CreateSession();
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            // The disabled Tell answers; no play directive, no queue state.
            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the disabled Tell must carry no directives");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("disabled", speech.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0,
                "the disabled Tell must leave no queue");
            Assert.Null(session.FullNowPlayingItem);
        }
        finally
        {
            // Code-review F2 (JF-805): capture-restore like the music twin below,
            // not the literal true this pin carried from the JF-795 round.
            Plugin.Instance!.Configuration.BooksEnabled = originalBooksEnabled;
        }
    }

    // ========== JF-805: the MusicAlbum confirm rides the ONE album play flow ==========

    /// <summary>
    /// The shared confirmed-album fixture (the JF-805 album twin of
    /// <see cref="SetupConfirmedBook"/>): a MusicAlbum served by GetItemById, a
    /// paging-honoring tracks executor over GetItemsResult (the composition's
    /// page plus the deep unpaged re-scan; any OTHER parent enumerates nothing),
    /// an unpaged GetItemList executor serving the whole album (the pre-JF-805
    /// confirm leg's own executor, so the red proofs exercise the real defect
    /// rather than an empty-mock artifact), and optional in-progress UserData
    /// on the track at <paramref name="progressTrackIndex"/>.
    /// </summary>
    private (MusicAlbum Album, List<BaseItem> Tracks) SetupConfirmedAlbum(
        int trackCount,
        int? progressTrackIndex = null)
    {
        var album = new MusicAlbum { Name = "Kind of Blue", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(lm => lm.GetItemById(album.Id)).Returns(album);

        List<BaseItem> tracks = Enumerable.Range(1, trackCount)
            .Select(i => (BaseItem)new Audio
            {
                Name = $"Kind of Blue track {i:00}",
                Id = Guid.NewGuid(),
                Album = album.Name,
                ParentId = album.Id,
                RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
            })
            .ToList();

        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.ParentId == album.Id
                ? new QueryResult<BaseItem>
                {
                    Items = tracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? tracks.Count).ToList(),
                    TotalRecordCount = tracks.Count
                }
                : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.ParentId == album.Id ? tracks : new List<BaseItem>());

        if (progressTrackIndex is int progressIdx)
        {
            var inProgress = new UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks
            };
            _userDataManagerMock
                .Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
                .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                    item.Id == tracks[progressIdx].Id ? inProgress : null);
        }

        return (album, tracks);
    }

    /// <summary>
    /// The shared confirm driver for the JF-805 MusicAlbum pins: builds the
    /// album disambiguation attrs (the payload names the ALBUM, PlayAlbum's
    /// matches are MusicAlbums), runs the yes confirm, snapshots the
    /// continuation the routed composition minted, and clears the store (the
    /// [Collection("Plugin")] isolation the JF-795 book twins keep by hand;
    /// callers assert on the snapshot because the store is already empty by
    /// the time they run).
    /// </summary>
    private async Task<(SkillResponse Response, SessionInfo Session, Context Context, QueueContinuation? Continuation)> ConfirmAlbumAsync(
        MusicAlbum album,
        Entities.User? user = null,
        Context? context = null,
        DeviceQueueManager? queueManager = null)
    {
        var attrs = CreateDisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { new() { Id = album.Id.ToString(), Name = album.Name } },
            0,
            "album");

        var handler = CreateHandler(queueManager);
        var session = CreateSession();
        var effectiveContext = context ?? CreateContext();
        try
        {
            SkillResponse response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                effectiveContext,
                user ?? TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, effectiveContext.System.Device.DeviceID!);
            return (response, session, effectiveContext, continuation);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, effectiveContext.System.Device.DeviceID!);
        }
    }

    /// <summary>
    /// JF-805 RED PROOF (resume axis, the album twin of the JF-795 book pin):
    /// a confirmed album with deep progress (track 22 of 26, beyond the initial
    /// page) must resume at the position-holding track exactly like the direct
    /// ask (the JF-796 deep-resume shape). The pre-fix confirm leg always
    /// launched albumItems[0] with no FindResumeTrackIndex scan, so the confirm
    /// answered a deep-progress album by restarting at track 1 at 0:00 (the
    /// confirm-must-match-ask rule on the resume axis). The album audio route's
    /// resume semantic is the queue-starting one (resumePosition false): the
    /// position-holding track launches at its beginning, offset 0.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_DeepProgress_ResumesAtPositionHoldingTrack()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(26, progressTrackIndex: 21);

        var (response, session, _, continuation) = await ConfirmAlbumAsync(album);

        // THE AXIS: the position-holding track launches, not track 1.
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(tracks[21].Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // The queue re-slices at the position-holding track (tracks 22 to 26);
        // nothing remains beyond it, so no continuation is minted.
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
        Assert.Equal(tracks[21].Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(tracks[21].Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(tracks[25].Id, session.NowPlayingQueue[4].Id);
        Assert.Null(continuation);
    }

    /// <summary>
    /// JF-805 RED PROOF (continuation axis): a confirmed multi-track album must
    /// mint the progressive QueueContinuation exactly like the direct ask. The
    /// pre-fix confirm leg queued the WHOLE album unpaged through the session
    /// queue only and never touched QueueContinuationStore or the device queue,
    /// so a "yes" diverged from the ask on the queue-completeness axis (no
    /// crash-recovery queue; on the seek route no continuation state at all).
    /// Pin: continuation minted with SourceType Album, ParentId the album,
    /// StartIndex 5 (page start 0 + page count 5), TotalCount 40.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_MintsQueueContinuation()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(40);

        var (response, session, _, continuation) = await ConfirmAlbumAsync(album);

        // The play itself launches track 1 fresh (no progress on the album).
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);

        // THE AXIS: the continuation is minted (pre-fix: null).
        Assert.NotNull(continuation);
        Assert.Equal("Album", continuation!.SourceType);
        Assert.Equal(album.Id, continuation.ParentId);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), continuation.StartIndex);
        Assert.Equal(40, continuation.TotalCount);
    }

    /// <summary>
    /// JF-805 companion pin: the continuation offset is RESUME-AWARE. A 40-track
    /// album confirmed with deep progress on track 22 re-slices the page at
    /// tracks 22 to 26, so the continuation must fetch from index 26 (page
    /// start 21 + page count 5) against the honest total 40, the same rebase
    /// the direct ask performs (JF-796).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_DeepProgress_ContinuationAtResumeAwareOffset()
    {
        (MusicAlbum album, List<BaseItem> _) = SetupConfirmedAlbum(40, progressTrackIndex: 21);

        var (_, _, _, continuation) = await ConfirmAlbumAsync(album);

        Assert.NotNull(continuation);
        Assert.Equal(26, continuation!.StartIndex);
        Assert.Equal(40, continuation.TotalCount);
    }

    /// <summary>
    /// JF-805 companion pin: a single-page album confirm is unchanged. A
    /// 3-track album plays the whole album (queue of 3, track 1 first) with no
    /// continuation, exactly the pre-fix outcome.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_SinglePage_PlaysWholeAlbumNoContinuation()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(3);

        var (response, session, _, continuation) = await ConfirmAlbumAsync(album);

        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(3, session.NowPlayingQueue.Count);
        Assert.Equal(tracks[2].Id, session.NowPlayingQueue[2].Id);
        Assert.Null(continuation);
    }

    /// <summary>
    /// JF-805 companion pin (no collateral on the announce path): with the
    /// default configuration (AnnounceAudioPlays off) the confirm stays silent,
    /// the pre-fix leg's speech shape (it never passed an announce locale). The
    /// routed composition passes the locale but the flag gates the announce, so
    /// confirm and ask speak the same silence.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_DefaultConfig_StaysSilent()
    {
        (MusicAlbum album, List<BaseItem> _) = SetupConfirmedAlbum(3);

        var (response, _, _, _) = await ConfirmAlbumAsync(album);

        Assert.NotNull(Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives!)));
        Assert.Null(response.Response.OutputSpeech);
    }

    /// <summary>
    /// JF-805 RED PROOF (the disabled axis, the JF-611/JF-795 shape): an album
    /// disambiguation prompt confirmed after an admin disabled music must
    /// answer the media-type-disabled Tell the direct ask gives, not launch
    /// the album through the confirm (the confirm-must-match-ask rule extends
    /// to the disabled answer). The pre-fix leg never gated.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_MusicDisabled_AnswersMediaTypeNotAvailable()
    {
        (MusicAlbum album, List<BaseItem> _) = SetupConfirmedAlbum(3);

        // Code-review F2: capture the pre-test value (the NativeControlsForBooks
        // rule at the file's video pins); a literal true here could force-flip a
        // sibling's legitimately-disabled flag mid-collection.
        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            var (response, session, _, _) = await ConfirmAlbumAsync(album);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the disabled Tell must carry no directives");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("not available", speech.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0,
                "the disabled Tell must leave no queue");
            Assert.Null(session.FullNowPlayingItem);
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    /// <summary>
    /// JF-805 companion pin (the seek-mode shape): the JF-625 tracker override
    /// rides the routed composition on the confirm path. A warm tracker at 5
    /// minutes into a 26-track album (track 2, 60s in) with deep UserData
    /// progress on track 22 stays the resume truth: the confirm launches the
    /// tracker's track on the VideoApp concat sliced at the tracker's position,
    /// never re-slicing at the deep UserData track (the JF-796 veto). The
    /// pre-fix leg had no tracker logic at all and launched track 1.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_SeekMode_WarmTrackerRidesTheComposition()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(26, progressTrackIndex: 21);

        var user = TestHelpers.CreateTestUser();
        user.VideoAppForAudio = true;

        using var trackerSwap = TestHelpers.WarmTrackerFiveMinutesIn(album.Id, "yes-album-confirm-tracker");
        var (response, session, _, _) = await ConfirmAlbumAsync(album, user, TestHelpers.CreateContextWithVideoApp());

        // Code-review F3: the single-directive invariant counts the WHOLE list
        // (an extra AudioPlayer directive alongside the VideoApp launch, the
        // double-launch shape, must red here, not hide behind the filter).
        var launch = Assert.IsType<VideoAppDirective.VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives!));
        // The tracker's mapping: track 1 runtime (4 min) + the 60s in-track partial.
        Assert.Contains($"start={TimeSpan.FromMinutes(4).Ticks + TimeSpan.FromSeconds(60).Ticks}", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.Equal(tracks[1].Name, launch.VideoItem.Metadata?.Title);

        // No re-slice at the deep UserData track: the queue keeps the page
        // window starting at the tracker's track (index 1).
        Assert.Equal(tracks[1].Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(tracks[1].Id, session.NowPlayingQueue[0].Id);
    }

    /// <summary>
    /// JF-805 pin (code-review F1, the device-queue axis): the confirm inherits
    /// the crash-recovery device queue from the ONE composition exactly like
    /// the direct ask. The pre-fix confirm leg never touched the device queue;
    /// with the routing in place, a confirm on a 40-track album must install
    /// the paged first five track ids on the confirming device's queue manager
    /// (the SetQueue write the composition owns), or a mid-album skill restart
    /// loses the crash-recovery resume the ask provides.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_WritesTheCrashRecoveryDeviceQueue()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(40);

        using var queueManager = TestHelpers.CreateDeviceQueueManager("yes-album-confirm-dq");
        var (_, _, context, _) = await ConfirmAlbumAsync(album, queueManager: queueManager);

        DeviceQueue deviceQueue = queueManager.GetOrCreateQueue(context.System.Device.DeviceID!);
        Assert.Equal(
            tracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).Select(t => t.Id.ToString()).ToList(),
            deviceQueue.ItemIds);
        Assert.Equal(0, deviceQueue.CurrentIndex);
    }

    /// <summary>
    /// JF-805 pin (code-review F4, the empty-album axis): an album whose track
    /// queries come back empty on BOTH arms (the split/malformed shapes where
    /// the JF-338 retry also finds nothing) must answer the NoSongsInAlbum
    /// Tell through the routing exactly like the direct ask: no directive, no
    /// queue state, and the speech names the album.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_EmptyAlbum_AnswersNoSongsInAlbum()
    {
        var album = new MusicAlbum { Name = "Empty Album", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(lm => lm.GetItemById(album.Id)).Returns(album);
        _libraryManagerMock
            .Setup(lm => lm.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var (response, session, _, continuation) = await ConfirmAlbumAsync(album);

        Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
            "the empty-album Tell must carry no directives");
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("no songs", speech.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(album.Name, speech.Text, StringComparison.Ordinal);
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0,
            "the empty-album Tell must leave no queue");
        Assert.Null(session.FullNowPlayingItem);
        Assert.Null(continuation);
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

    // ========== JF-806: the song/artist confirm legs' music gate + the confirm legs' warming gate ==========

    /// <summary>
    /// The shared warming-index driver for the JF-806 confirm-leg pins: drives a
    /// disambiguation confirm against a handler whose artist index is present but
    /// still loading, asserting the leg refuses at entry instead of running the
    /// composition's cold-database fetches (the JF-419 live-incident class; the
    /// SkillWarmingUpTests entry-gate idiom on the attrs overload).
    /// </summary>
    private async Task AssertConfirmThrowsWarmingAsync(
        IArtistIndex warmingIndex,
        Dictionary<string, object> attrs)
    {
        var handler = CreateHandler(artistIndex: warmingIndex);

        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() => handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None));
        Assert.StartsWith("artist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, object> CreateSingleMatchAttrs(Guid itemId, string name, string type)
        => CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { new() { Id = itemId.ToString(), Name = name } }, 0, type);

    private static IArtistIndex WarmingArtistIndex()
        => Mock.Of<IArtistIndex>(i => i.IsReady == false);

    private static IArtistIndex ReadyArtistIndex()
        => Mock.Of<IArtistIndex>(i => i.IsReady == true);

    /// <summary>
    /// JF-806 RED PROOF (the disabled axis, song leg): a song disambiguation
    /// prompt confirmed after an admin disabled music must answer the
    /// media-type-disabled Tell the direct ask gives (PlaySong's JF-467 entry
    /// gate), not launch the song through the confirm. The pre-fix arm never
    /// gated, so a "yes" played media the ask would refuse.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationSongType_SongConfirm_MusicDisabled_AnswersMediaTypeNotAvailable()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);
        var attrs = CreateSingleMatchAttrs(songId, song.Name, DisambiguationHelper.MediaTypeSong);

        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            var handler = CreateHandler();
            var session = CreateSession();
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the disabled Tell must carry no directives");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("not available", speech.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0,
                "the disabled Tell must leave no queue");
            Assert.Null(session.FullNowPlayingItem);
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    /// <summary>
    /// JF-806 RED PROOF (the disabled axis, artist leg): an artist
    /// disambiguation prompt confirmed after an admin disabled music must answer
    /// the media-type-disabled Tell the direct ask gives (PlayArtistSongs'
    /// JF-467 entry gate), not run the artist catalog query and launch through
    /// the confirm.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationArtistType_ArtistConfirm_MusicDisabled_AnswersMediaTypeNotAvailable()
    {
        var artistId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Test Artist", Id = artistId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(artistId)).Returns(artist);
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Name = "Song", Id = Guid.NewGuid() } });
        var attrs = CreateSingleMatchAttrs(artistId, artist.Name, DisambiguationHelper.MediaTypeArtist);

        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            var handler = CreateHandler();
            var session = CreateSession();
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                session,
                attrs,
                CancellationToken.None);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the disabled Tell must carry no directives");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("not available", speech.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0,
                "the disabled Tell must leave no queue");
            Assert.Null(session.FullNowPlayingItem);
            _libraryManagerMock.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never,
                "the disabled Tell must not run the artist catalog query");
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    /// <summary>
    /// JF-806 RED PROOF (the warming axis, album leg; folded from the JF-805
    /// gate marker): a MusicAlbum confirm running while the artist index is
    /// still loading (the plugin restarted mid-session) must refuse at leg
    /// entry, the same cold-database protection the direct ask pays
    /// (PlayAlbum's Layer-1 gate; the artist index stands in for the shared
    /// cold database). The pre-fix confirm ran the routed composition's paged
    /// fetch, deep unpaged re-scan, and per-track UserData reads ungated,
    /// inside the Alexa window.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_WhileIndexWarming_ThrowsAtEntry()
    {
        (MusicAlbum album, List<BaseItem> _) = SetupConfirmedAlbum(3);
        var attrs = CreateSingleMatchAttrs(album.Id, album.Name, DisambiguationHelper.MediaTypeAlbum);

        await AssertConfirmThrowsWarmingAsync(WarmingArtistIndex(), attrs);
    }

    /// <summary>
    /// JF-806 RED PROOF (the warming axis, book leg; the JF-795 twin shape):
    /// a book confirm running while the artist index is still loading must
    /// refuse at leg entry before the resolved-book composition's paged chapter
    /// fetch and per-chapter UserData reads (the JF-805 marker folded this leg
    /// into the warming axis even though the book ASK carries no Layer-1 gate
    /// of its own: the confirm's composition is the widened surface).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookConfirm_WhileIndexWarming_ThrowsAtEntry()
    {
        (List<BaseItem> _, Guid bookFolderId, _) = SetupConfirmedBook();
        var attrs = CreateSingleMatchAttrs(bookFolderId, "Measure What Matters", DisambiguationHelper.MediaTypeAlbum);

        await AssertConfirmThrowsWarmingAsync(WarmingArtistIndex(), attrs);
    }

    /// <summary>
    /// JF-806 RED PROOF (the warming axis, artist leg): an artist confirm
    /// running while the artist index is still loading must refuse before the
    /// leg's UNPAGED whole-catalog ArtistIds query (the heaviest cold-database
    /// surface any confirm leg runs), the same protection PlayArtistSongs pays
    /// at its own entry.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationArtistType_ArtistConfirm_WhileIndexWarming_ThrowsAtEntry()
    {
        var artistId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Test Artist", Id = artistId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(artistId)).Returns(artist);
        // Served so the pre-fix RED failure is the clean no-throw (the ungated
        // query ran and played), not a mock-default null crashing the leg.
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Name = "Song", Id = Guid.NewGuid() } });
        var attrs = CreateSingleMatchAttrs(artistId, artist.Name, DisambiguationHelper.MediaTypeArtist);

        await AssertConfirmThrowsWarmingAsync(WarmingArtistIndex(), attrs);
    }

    /// <summary>
    /// JF-806 companion pin (gate transparency): a READY artist index leaves
    /// the album confirm unchanged; the gate only converts the warming window,
    /// never the warm path.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_ReadyIndex_PlaysUnchanged()
    {
        (MusicAlbum album, List<BaseItem> tracks) = SetupConfirmedAlbum(3);

        var handler = CreateHandler(artistIndex: ReadyArtistIndex());
        var session = CreateSession();
        var attrs = CreateSingleMatchAttrs(album.Id, album.Name, DisambiguationHelper.MediaTypeAlbum);
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            attrs,
            CancellationToken.None);

        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Equal(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(3, session.NowPlayingQueue.Count);
    }

    /// <summary>
    /// JF-806 placement pin (the point-lookup legs stay ungated): the song
    /// confirm's database surface is the ALREADY-RESOLVED single item (the
    /// confirm resolved the match before dispatch; BuildSingleSongResponse
    /// issues no library query), so a warming index must NOT refuse it: the
    /// ask's own warming gate protects the SEARCH the confirm has already
    /// completed, and over-gating here would refuse a bounded path that works.
    /// </summary>
    /// <summary>
    /// Gate-marker tail F3: the song leg's INTERSECTION pin (the documented bounded
    /// divergence, machine-locked like its siblings). With music disabled AND the
    /// index warming, the ask answers SkillWarmingUp (its warming gate precedes its
    /// music gate in PlaySongIntentHandler) while the confirm answers
    /// MediaTypeNotAvailable (its only gate is the music one; the confirm's database
    /// surface is the already-resolved single item, so no warming gate exists to
    /// answer first). Both answers are terminal refusals differing only in string -
    /// the recorded bounded divergence. A future edit that inserts a warming gate
    /// before the song leg's music gate, or removes the music gate, REDS here.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationSongType_MusicDisabledWhileIndexWarming_AnswersMusicTellNotWarming()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);
        var attrs = CreateSingleMatchAttrs(songId, song.Name, DisambiguationHelper.MediaTypeSong);

        var handler = CreateHandler(artistIndex: WarmingArtistIndex());
        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            SkillResponse response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                CreateSession(),
                attrs,
                CancellationToken.None);

            Assert.NotNull(response.Response?.OutputSpeech);
            Assert.Empty(response.Response.Directives);
            Assert.DoesNotContain("SkillWarmingUp", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
            Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    [Fact]
    public async Task HandleAsync_DisambiguationSongType_SongConfirm_WhileIndexWarming_PlaysSingleSong()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);
        var attrs = CreateSingleMatchAttrs(songId, song.Name, DisambiguationHelper.MediaTypeSong);

        var handler = CreateHandler(artistIndex: WarmingArtistIndex());
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }

    /// <summary>
    /// JF-806 placement pin (the pagination "yes" stays ungated): the
    /// pagination continuation resolves only the page's item IDs via point
    /// lookups (ListPaginationHelper.BuildNextPageResponse, the SAME shared
    /// helper the ungated ShowMoreIntent twin rides), so a warming index must
    /// not refuse it; the dispatch order (pagination before disambiguation)
    /// keeps it structurally out of the confirm legs' gates.
    /// </summary>
    [Fact]
    public async Task HandleAsync_PaginationContinuation_WhileIndexWarming_AnswersNextPage()
    {
        var first = new Audio { Name = "Warming Page Item", Id = Guid.NewGuid() };
        var second = new Audio { Name = "Later Item", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(lm => lm.GetItemById(first.Id)).Returns(first);
        _libraryManagerMock.Setup(lm => lm.GetItemById(second.Id)).Returns(second);

        var attrs = new Dictionary<string, object>();
        ListPaginationHelper.WriteState(
            attrs,
            ListPaginationHelper.ListType.BrowseLibrary,
            new[] { first.Id.ToString(), second.Id.ToString() },
            0,
            1);

        var handler = CreateHandler(artistIndex: WarmingArtistIndex());
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Asks<PlainTextOutputSpeech>();
        Assert.Contains(first.Name, speech.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-806 code-review F2 (the gate-ORDER contracts): the album leg orders
    /// warming BEFORE the music gate, mirroring PlayAlbum's own order, so in the
    /// warming+disabled intersection the confirm answers the same SkillWarmingUp
    /// refusal the ask does. Locks the order against a future normalization that
    /// would flip it while every single-axis pin stays green.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_WarmingAndMusicDisabled_AnswersWarmingFirst()
    {
        (MusicAlbum album, List<BaseItem> _) = SetupConfirmedAlbum(3);
        var attrs = CreateSingleMatchAttrs(album.Id, album.Name, DisambiguationHelper.MediaTypeAlbum);

        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            await AssertConfirmThrowsWarmingAsync(WarmingArtistIndex(), attrs);
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    /// <summary>
    /// JF-806 code-review F2 (the gate-ORDER contracts): the artist leg orders
    /// the music gate BEFORE warming, mirroring PlayArtistSongs' own order, so
    /// the warming+disabled intersection answers MediaTypeNotAvailable, not the
    /// warming refusal.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationArtistType_ArtistConfirm_WarmingAndMusicDisabled_AnswersDisabledFirst()
    {
        var artistId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Test Artist", Id = artistId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(artistId)).Returns(artist);
        var attrs = CreateSingleMatchAttrs(artistId, artist.Name, DisambiguationHelper.MediaTypeArtist);

        bool originalMusicEnabled = Plugin.Instance!.Configuration.MusicEnabled;
        Plugin.Instance!.Configuration.MusicEnabled = false;
        try
        {
            var handler = CreateHandler(artistIndex: WarmingArtistIndex());
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                CreateSession(),
                attrs,
                CancellationToken.None);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the intersection must answer the disabled Tell, not launch");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("not available", speech.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Plugin.Instance!.Configuration.MusicEnabled = originalMusicEnabled;
        }
    }

    /// <summary>
    /// JF-806 code-review F2 (the gate-ORDER contracts): the book leg orders
    /// the books gate BEFORE warming (the ungated ask would answer
    /// FeatureDisabled in the intersection, so the confirm matches it).
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_BookConfirm_WarmingAndBooksDisabled_AnswersDisabledFirst()
    {
        (List<BaseItem> _, Guid bookFolderId, _) = SetupConfirmedBook();
        var attrs = CreateSingleMatchAttrs(bookFolderId, "Measure What Matters", DisambiguationHelper.MediaTypeAlbum);

        bool originalBooksEnabled = Plugin.Instance!.Configuration.BooksEnabled;
        Plugin.Instance!.Configuration.BooksEnabled = false;
        try
        {
            var handler = CreateHandler(artistIndex: WarmingArtistIndex());
            var response = await handler.HandleAsync(
                CreateYesIntentRequest(),
                CreateContext(),
                TestHelpers.CreateTestUser(),
                CreateSession(),
                attrs,
                CancellationToken.None);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the intersection must answer the disabled Tell, not launch");
            var speech = response.Tells<PlainTextOutputSpeech>();
            Assert.Contains("disabled", speech.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Plugin.Instance!.Configuration.BooksEnabled = originalBooksEnabled;
        }
    }

    /// <summary>
    /// JF-806 code-review F4 (point-lookup transparency): the video confirm
    /// launches the already-resolved item with no library query and no warming
    /// gate (its ask is Layer-1 ungated too); a warming index must not refuse
    /// it. Locks the decision so a future warming sweep must make it
    /// deliberately.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DisambiguationVideoType_VideoConfirm_WhileIndexWarming_LaunchesVideo()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "Test Movie", Id = videoId };
        _libraryManagerMock.Setup(lm => lm.GetItemById(videoId)).Returns(movie);
        var attrs = CreateSingleMatchAttrs(videoId, movie.Name, DisambiguationHelper.MediaTypeVideo);

        var handler = CreateHandler(artistIndex: WarmingArtistIndex());
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
    }

    /// <summary>
    /// JF-806 code-review F4 (point-lookup transparency): the resume
    /// confirmation resolves one item by ID and builds the launch (no library
    /// query), so a warming index must not refuse it; its intent-handler twin
    /// (ResumeIntent) is Layer-1 ungated too.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ResumeConfirmation_WhileIndexWarming_LaunchesAudio()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "FreeCommerce",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));
        _libraryManagerMock.Setup(lm => lm.GetItemById(id)).Returns(episode);

        var handler = CreateHandler(artistIndex: WarmingArtistIndex());
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CreateResumeAttrs(id, 300000),
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }
}
