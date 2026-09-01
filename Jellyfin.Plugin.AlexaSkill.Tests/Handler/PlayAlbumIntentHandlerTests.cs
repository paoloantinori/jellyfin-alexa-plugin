using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayAlbumIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;

    public PlayAlbumIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "playalbum-tests");
    }

    private PlayAlbumIntentHandler CreateHandler()
    {
        return new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? album = null, string? musician = null)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (album != null)
        {
            intent.Slots["album"] = new global::Alexa.NET.Request.Slot { Name = "album", Value = album };
        }

        if (musician != null)
        {
            intent.Slots["musician"] = new global::Alexa.NET.Request.Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupAlbumsAndTracks(List<BaseItem> albums, QueryResult<BaseItem>? tracks = null, List<InternalItemsQuery>? queries = null)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                return albums;
            });
        if (tracks != null)
        {
            _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
                .Returns(tracks);
        }
    }

    /// <summary>
    /// Builds a MusicAlbum plus <paramref name="trackCount"/> Audio tracks linked via the Album
    /// metadata name (the linkage GetAlbumTrackCountsAsync groups by). The first track's name is
    /// <paramref name="firstTrackName"/> so the played album is identifiable from the AudioPlayer token.
    /// </summary>
    private static (MusicAlbum Album, List<BaseItem> Tracks) MakeRelease(string name, int year, int trackCount, string firstTrackName)
    {
        var album = new MusicAlbum { Name = name, Id = Guid.NewGuid(), ProductionYear = year };
        var tracks = new List<BaseItem>();
        for (int i = 0; i < trackCount; i++)
        {
            tracks.Add(new Audio
            {
                Name = i == 0 ? firstTrackName : $"{name} track {i + 1}",
                Id = Guid.NewGuid(),
                Album = name,
                ParentId = album.Id
            });
        }

        return (album, tracks);
    }

    /// <summary>
    /// Mocks the JF-411 indefinite album-by-artist flow: artist lookup, the artist's albums in the
    /// given (insertion) order, the tracks feeding the JF-427 count query, and per-album playback
    /// results. Queries are recorded into <paramref name="queries"/> (when given) for assertions.
    /// </summary>
    private void SetupIndefiniteAlbumCatalog(
        BaseItem artist,
        List<BaseItem> artistAlbums,
        List<BaseItem> allTracks,
        IReadOnlyDictionary<Guid, BaseItem> firstTrackByAlbumId,
        List<InternalItemsQuery>? queries = null)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true)
                {
                    return artistAlbums;
                }

                if (q.IncludeItemTypes?.Contains(BaseItemKind.Audio) == true)
                {
                    return allTracks;
                }

                return new List<BaseItem>();
            });

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                firstTrackByAlbumId.TryGetValue(q.ParentId, out BaseItem? track)
                    ? new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });
    }

    private static async Task<string> GetPlayedTrackTokenAsync(
        PlayAlbumIntentHandler handler,
        IntentRequest request,
        SessionInfo session)
    {
        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        return playDirective!.AudioItem.Stream.Token;
    }

    /// <summary>
    /// Runs the indefinite album-by-artist flow once per insertion order and asserts the SAME
    /// album plays every time (JF-427: database row order must not decide the pick).
    /// </summary>
    private async Task AssertPicksSameTrackAcrossOrdersAsync(
        PlayAlbumIntentHandler handler,
        IntentRequest request,
        BaseItem artist,
        MusicAlbum[] albums,
        List<BaseItem> allTracks,
        IReadOnlyDictionary<Guid, BaseItem> firstTrackByAlbum,
        string expectedToken,
        int[][] orders)
    {
        foreach (int[] order in orders)
        {
            SetupIndefiniteAlbumCatalog(
                artist,
                order.Select(i => (BaseItem)albums[i]).ToList(),
                allTracks,
                firstTrackByAlbum);

            string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

            Assert.True(
                token == expectedToken,
                $"insertion order [{string.Join(",", order)}] picked token {token}, expected {expectedToken}");
        }
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_MultiReleaseArtist_PicksMostTracksAlbum()
    {
        // JF-427: "un disco di X" must not play an arbitrary row of the artist's catalog.
        // Policy: the release with the MOST tracks wins (the 12-track studio album) even
        // over a NEWER 10-track live sampler (a year-first policy would pick the live
        // album) and a 2-track single.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (liveAlbum, liveTracks) = MakeRelease("BBC Sessions", 1998, 10, "Live First");
        var (studioAlbum, studioTracks) = MakeRelease("Studio Album", 1995, 12, "Studio First");
        var (single, singleTracks) = MakeRelease("Single", 1996, 2, "Single First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [liveAlbum.Id] = liveTracks[0],
            [studioAlbum.Id] = studioTracks[0],
            [single.Id] = singleTracks[0]
        };
        var queries = new List<InternalItemsQuery>();
        SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { liveAlbum, studioAlbum, single },
            liveTracks.Concat(studioTracks).Concat(singleTracks).ToList(),
            firstTrackByAlbum,
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(studioTracks[0].Id.ToString(), token);

        // The resolution query must carry an explicit deterministic OrderBy (JF-427: the
        // query previously had none).
        InternalItemsQuery? resolutionQuery = queries.FirstOrDefault(q =>
            q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == null);
        Assert.NotNull(resolutionQuery);
        Assert.NotNull(resolutionQuery.OrderBy);
        Assert.Equal(ItemSortBy.ProductionYear, resolutionQuery.OrderBy[0].Item1);
        Assert.Equal(SortOrder.Descending, resolutionQuery.OrderBy[0].Item2);
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_ShuffledAlbumRows_PickIsStable()
    {
        // JF-427 AC#2: database row order (which a rescan changes) must not decide the
        // pick. Same album set, every insertion order, same album played.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (liveAlbum, liveTracks) = MakeRelease("BBC Sessions", 1998, 10, "Live First");
        var (studioAlbum, studioTracks) = MakeRelease("Studio Album", 1995, 12, "Studio First");
        var (single, singleTracks) = MakeRelease("Single", 1996, 2, "Single First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [liveAlbum.Id] = liveTracks[0],
            [studioAlbum.Id] = studioTracks[0],
            [single.Id] = singleTracks[0]
        };
        MusicAlbum[] albums = { liveAlbum, studioAlbum, single };

        await AssertPicksSameTrackAcrossOrdersAsync(
            handler, request, artist, albums,
            liveTracks.Concat(studioTracks).Concat(singleTracks).ToList(),
            firstTrackByAlbum,
            studioTracks[0].Id.ToString(),
            new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } });
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_EqualTrackCounts_TieBreaksByNewestProductionYear()
    {
        // JF-427 policy tie-break 1: equal track counts fall to the newest ProductionYear.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (older, olderTracks) = MakeRelease("Older Album", 1995, 10, "Older First");
        var (newer, newerTracks) = MakeRelease("Newer Album", 2001, 10, "Newer First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [older.Id] = olderTracks[0],
            [newer.Id] = newerTracks[0]
        };
        MusicAlbum[] albums = { older, newer };

        await AssertPicksSameTrackAcrossOrdersAsync(
            handler, request, artist, albums,
            olderTracks.Concat(newerTracks).ToList(),
            firstTrackByAlbum,
            newerTracks[0].Id.ToString(),
            new[] { new[] { 0, 1 }, new[] { 1, 0 } });
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_EqualCountsAndYear_TieBreaksByNameAscending()
    {
        // JF-427 policy tie-break 2: equal counts and year fall to Name ascending, so the
        // pick stays deterministic even when the first two keys tie.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (zebra, zebraTracks) = MakeRelease("Zebra Album", 2000, 5, "Zebra First");
        var (alpha, alphaTracks) = MakeRelease("Alpha Album", 2000, 5, "Alpha First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [zebra.Id] = zebraTracks[0],
            [alpha.Id] = alphaTracks[0]
        };
        MusicAlbum[] albums = { zebra, alpha };

        await AssertPicksSameTrackAcrossOrdersAsync(
            handler, request, artist, albums,
            zebraTracks.Concat(alphaTracks).ToList(),
            firstTrackByAlbum,
            alphaTracks[0].Id.ToString(),
            new[] { new[] { 0, 1 }, new[] { 1, 0 } });
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_PromptsDisambiguation()
    {
        // JF-341: distinct-name multi-match (e.g. "Greatest Hits" vs "Biggest Hits") must
        // prompt the user (AskFirstMatch), not silently auto-play the best-scoring one.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(new List<BaseItem> { album1, album2 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // AskFirstMatch keeps the session open + does NOT auto-play.
        Assert.False(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_AutoPlayUser_AutoPlaysFirst()
    {
        // JF-341 review: an AutoPlay user (opted out of disambiguation prompts) auto-plays
        // even for distinct-name collisions.
        var user = TestHelpers.CreateTestUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_MultipleSameNameAlbums_AutoPlaysFirst()
    {
        // JF-341: same-name duplicates (e.g. two "Jazz Cafe" disc-albums) auto-play the first
        // -- a "Jazz Cafe or Jazz Cafe?" prompt would be useless.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "jazz cafe");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AutoPlays()
    {
        // Regression guard (AC#5): single-match albums still auto-play.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_InteriorContainmentFuzzyMatch_DoesNotAutoPlay()
    {
        // JF-408 incident replay (live 2026-08-28): query "walls for cup" (ASR for "Waltz for
        // Koop") fuzzy-matched album "O" at ContainmentScore because the query contains an 'o'
        // inside "for", and auto-played it on-device. The fuzzy match is returned by the recall
        // layer; the auto-play decision must reject an interior-only containment.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "walls for cup");
        SetupUserMock();

        // Exact search (SearchTerm set) must miss; the fuzzy fallback's full-catalog scan
        // (SearchTerm null) returns the degenerate 1-char album.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "O", Id = Guid.NewGuid() } }
                : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_PlaysArtistsAlbum()
    {
        // JF-411: "un disco dei Koop" (indefinite album-by-artist) fills only the musician
        // slot. The handler must resolve the artist's album and play it, not discard the
        // artist behind an album-name reprompt. The album resolution must filter on
        // ALBUM artists (AlbumArtistIds): ArtistIds matches any album CONTAINING a track
        // by the artist, which on the live library picked a compilation featuring Koop
        // over Koop's own album.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Baby", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                return new List<BaseItem> { album };
            });
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);

        // The indefinite-resolution query (the MusicAlbum query with no SearchTerm) must
        // filter on album artists.
        InternalItemsQuery? resolutionQuery = queries.FirstOrDefault(q =>
            q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == null);
        Assert.NotNull(resolutionQuery);
        Assert.NotNull(resolutionQuery.AlbumArtistIds);
        Assert.Contains(artist.Id, resolutionQuery.AlbumArtistIds);
    }

    [Fact]
    public async Task HandleAsync_BothSlotsEmpty_ElicitsAlbumViaDialogDirective()
    {
        // JF-422: a bare "riproduci un album" arrives with BOTH slots empty. Ask WHICH
        // ALBUM: the common answer is a title, which feeds the album-title search. The
        // previous artist-first order (kept from the 2026-08-28 "un disco dei" ASR
        // swallow) captured a title answer into the musician slot and dead-ended in
        // NotFoundAlbumByArtist for an album that exists.
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal("album", elicit.SlotToElicit);
        Assert.Equal("PlayAlbumIntent", elicit.UpdatedIntent.Name);
        // Amazon requires updatedIntent to declare EVERY intent slot (live INVALID_RESPONSE
        // 2026-08-28 21:17: "All slots must be defined... Missing: album").
        Assert.Equal(new[] { "album", "musician" }, elicit.UpdatedIntent.Slots.Keys.OrderBy(k => k).ToArray());
        // JF-398: the elicit owns no flow state, so every OTHER flow's keys must be
        // marked for removal (no stale resume/disambiguation/pagination rides along).
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey(Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.SessionAttributeRemoval.MarkerKey), "elicit must mark other flows inactive");
    }

    [Fact]
    public async Task HandleAsync_BothSlotsEmpty_AlbumTitleAnswer_ResolvesAlbumByTitle()
    {
        // JF-422 defect 1: the both-empty elicit targets the ALBUM slot (asserted by
        // HandleAsync_BothSlotsEmpty_ElicitsAlbumViaDialogDirective), so a title answer
        // ("the dark side of the moon") arrives in the album slot mid-dialog and must
        // drive an ALBUM search. Under the old artist-first order the title was captured
        // as the musician, ArtistSearch found nothing, and the user got a terminal
        // NotFoundAlbumByArtist.
        var handler = CreateHandler();
        SetupUserMock();

        var answer = CreateIntentRequest(album: "the dark side of the moon");
        answer.DialogState = "IN_PROGRESS";
        var (album, albumTracks) = MakeRelease("The Dark Side of the Moon", 1973, 1, "Speak to Me");
        var queries = new List<InternalItemsQuery>();
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = albumTracks, TotalRecordCount = albumTracks.Count },
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, answer, CreateSession());

        Assert.Equal(albumTracks[0].Id.ToString(), token);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
        InternalItemsQuery? albumQuery = queries.FirstOrDefault(q =>
            q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm != null);
        Assert.NotNull(albumQuery);
        Assert.Equal("the dark side of the moon", albumQuery.SearchTerm);
    }

    [Fact]
    public async Task HandleAsync_DialogInProgressWithMusician_PlaysArtistsAlbum_NoTitlePrompt()
    {
        // JF-422 defect 2: the musician answer returns with dialogState IN_PROGRESS and
        // must reach the JF-411 album-by-artist resolution and PLAY an album by that
        // artist. The old IN_PROGRESS branch re-elicted the title first, asking the
        // "any album by X" user a question they cannot answer.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "queen");
        request.DialogState = "IN_PROGRESS";
        SetupUserMock();

        var artist = new MusicArtist { Name = "Queen", Id = Guid.NewGuid() };
        var (album, albumTracks) = MakeRelease("A Night at the Opera", 1975, 1, "Bohemian Rhapsody");
        var queries = new List<InternalItemsQuery>();
        SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] },
            queries);

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        // The play came from the JF-411 resolution, not from a title search.
        InternalItemsQuery? resolutionQuery = queries.FirstOrDefault(q =>
            q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == null);
        Assert.NotNull(resolutionQuery);
        Assert.NotNull(resolutionQuery.AlbumArtistIds);
        Assert.Contains(artist.Id, resolutionQuery.AlbumArtistIds);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOffByDefault_SilentLaunch()
    {
        // JF-353 AC#4 / JF-352.4: audio plays are silent by default. Even with the video-launch
        // toggle (DefaultAnnounceNowPlaying) on, a music play must stay silent unless the separate
        // AnnounceAudioPlays flag is opted in. This guards "no behavior change for existing users"
        // on the most frequent play paths.
        _config.DefaultAnnounceNowPlaying = true; // video/book toggle on
        _config.AnnounceAudioPlays = false; // audio opt-in default
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOn_SpeaksNowPlaying()
    {
        // When the user opts into audio announces (AnnounceAudioPlays = true), a successful
        // album play attaches the now-playing announce to OutputSpeech.
        _config.AnnounceAudioPlays = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        // The now-playing announce speaks the track that starts playing ("Track 1"), since the
        // AudioPlayer token is the first track of the album, not the album container itself.
        Assert.Contains("Track 1", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOff_SilentLaunch()
    {
        // With AnnounceAudioPlays explicitly off, the launch is silent (no OutputSpeech)
        // but playback still starts (AudioPlayer.Play directive present).
        _config.AnnounceAudioPlays = false;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }
}
