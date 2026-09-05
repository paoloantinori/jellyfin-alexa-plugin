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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayAlbumIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;

    public PlayAlbumIntentHandlerTests()
    {
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "playalbum-tests");
    }

    private PlayAlbumIntentHandler CreateHandler(IArtistIndex? artistIndex = null)
    {
        return new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            artistIndex);
    }

    private static IntentRequest CreateIntentRequest(string? album = null, string? musician = null, string locale = "en-US")
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

        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupAlbumsAndTracks(List<BaseItem> albums, QueryResult<BaseItem>? tracks = null, List<InternalItemsQuery>? queries = null)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                return albums;
            });
        if (tracks != null)
        {
            _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
                .Returns(tracks);
        }
    }

    /// <summary>
    /// Builds a MusicAlbum plus <paramref name="trackCount"/> Audio tracks. By default the
    /// tracks are linked to the album via ParentId (the entity link the JF-443 primary
    /// COUNT keys on; the raw Album tag is irrelevant there, so
    /// <paramref name="rawAlbumTag"/> can deliberately mismatch it). With
    /// <paramref name="breakParentLink"/> the tracks are parented to an unrelated folder
    /// and only the raw Album tag links them (the JF-338 malformed-folder shape the
    /// AlbumIds fallback covers). The first track's name is
    /// <paramref name="firstTrackName"/> so the played album is identifiable from the AudioPlayer token.
    /// </summary>
    private static (MusicAlbum Album, List<BaseItem> Tracks) MakeRelease(string name, int year, int trackCount, string firstTrackName, string? rawAlbumTag = null, bool breakParentLink = false)
    {
        var album = new MusicAlbum { Name = name, Id = Guid.NewGuid(), ProductionYear = year };
        Guid trackParentId = breakParentLink ? Guid.NewGuid() : album.Id;
        var tracks = new List<BaseItem>();
        for (int i = 0; i < trackCount; i++)
        {
            tracks.Add(new Audio
            {
                Name = i == 0 ? firstTrackName : $"{name} track {i + 1}",
                Id = Guid.NewGuid(),
                Album = rawAlbumTag ?? name,
                ParentId = trackParentId
            });
        }

        return (album, tracks);
    }

    private async Task<string> GetPlayedTrackTokenAsync(
        PlayAlbumIntentHandler handler,
        IntentRequest request,
        SessionInfo session)
    {
        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);
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
            _fx.SetupIndefiniteAlbumCatalog(
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
        _fx.SetupUserMock();

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
        _fx.SetupIndefiniteAlbumCatalog(
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
        _fx.SetupUserMock();

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
        _fx.SetupUserMock();

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
        _fx.SetupUserMock();

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
    public async Task HandleAsync_MusicianOnly_RawAlbumTagMismatch_WellFormedAlbumWins()
    {
        // JF-443: the PRIMARY count keys on the ParentId entity link, not the raw Album
        // tag. The old sweep grouped tracks under the raw tag and looked the count up by
        // MusicAlbum.Name with OrdinalIgnoreCase, so a "Name (Disc 1)" tag (or a trailing
        // space / accent variant) zeroed a well-formed album and flipped the pick to any
        // exactly-matching release; an AlbumIds-only count would miss the same way
        // (server-side AlbumIds matches the raw tag against the album Name). Here the
        // 12-track studio release carries a mismatched raw tag but a proper ParentId
        // link and must STILL outrank the newer 10-track live album.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (studio, studioTracks) = MakeRelease("Studio Album", 1995, 12, "Studio First", rawAlbumTag: "Studio Album (Disc 1)");
        var (live, liveTracks) = MakeRelease("Live Album", 2001, 10, "Live First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [studio.Id] = studioTracks[0],
            [live.Id] = liveTracks[0]
        };
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { studio, live },
            studioTracks.Concat(liveTracks).ToList(),
            firstTrackByAlbum,
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(studioTracks[0].Id.ToString(), token);

        // The count queries must be COUNT-only ParentId queries: ParentId=album.Id,
        // IncludeItemTypes=Audio, Limit=0 (read TotalRecordCount; no rows materialized).
        var countQueries = queries.Where(q => q.Limit == 0 && q.ParentId != Guid.Empty).ToList();
        Assert.Equal(2, countQueries.Count);
        Assert.All(countQueries, q =>
        {
            Assert.Equal(0, q.Limit);
            Assert.NotNull(q.IncludeItemTypes);
            Assert.Contains(BaseItemKind.Audio, q.IncludeItemTypes!);
        });
        Assert.Contains(countQueries, q => q.ParentId == studio.Id);
        Assert.Contains(countQueries, q => q.ParentId == live.Id);
        // Both albums are well-formed (ParentId count > 0), so the AlbumIds fallback
        // never fires.
        Assert.DoesNotContain(queries, q => q.Limit == 0 && q.AlbumIds is { Length: > 0 });
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_MalformedParentLink_CountsFallBackToAlbumIds()
    {
        // JF-338 shape: a malformed/split album whose tracks are parented to an
        // unrelated folder (ParentId link broken) but still carry the album's raw Album
        // tag. The ParentId count returns 0, so the AlbumIds fallback must fire and
        // count the release by its tag; the 12-track malformed release must still
        // outrank the well-formed newer 10-track live album.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (studio, studioTracks) = MakeRelease("Studio Album", 1995, 12, "Studio First", breakParentLink: true);
        var (live, liveTracks) = MakeRelease("Live Album", 2001, 10, "Live First");
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>
        {
            [studio.Id] = studioTracks[0],
            [live.Id] = liveTracks[0]
        };
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { studio, live },
            studioTracks.Concat(liveTracks).ToList(),
            firstTrackByAlbum,
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        // The malformed 12-track release wins via the AlbumIds tag count. (The mock's
        // playback lookup is keyed by album id for both the ParentId and AlbumIds
        // shapes, so which play-path query served the first track is not asserted here;
        // the count-query shapes below are the point.)
        Assert.Equal(studioTracks[0].Id.ToString(), token);

        // The malformed album was counted by BOTH shapes: the ParentId primary returned
        // 0 (link broken) and the AlbumIds fallback fired. The well-formed live album
        // needed only the ParentId count.
        Assert.Contains(queries, q => q.Limit == 0 && q.ParentId == studio.Id);
        Assert.Contains(queries, q => q.Limit == 0 && q.AlbumIds is { Length: > 0 } && q.AlbumIds[0] == studio.Id);
        Assert.Contains(queries, q => q.Limit == 0 && q.ParentId == live.Id);
        Assert.DoesNotContain(queries, q => q.Limit == 0 && q.AlbumIds is { Length: > 0 } && q.AlbumIds[0] == live.Id);
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_HundredPlusAlbumTail_CountQueriesCappedToTopTwelve()
    {
        // JF-443: the COUNT queries are capped to the top-12 candidates in the
        // deterministic order (newest year, then name, then id). The oldest album here has
        // by far the most tracks (99) but falls OUTSIDE the cap, ranks as 0 tracks, and
        // must not win; exactly 12 count queries are issued (was: one query materializing
        // every Audio row of the artist's catalog).
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var artistAlbums = new List<BaseItem>();
        var allTracks = new List<BaseItem>();
        var firstTrackByAlbum = new Dictionary<Guid, BaseItem>();
        for (int i = 0; i < 12; i++)
        {
            string name = $"Album {i:D2}";
            var (album, tracks) = MakeRelease(name, 1995, 2, $"{name} First");
            artistAlbums.Add(album);
            allTracks.AddRange(tracks);
            firstTrackByAlbum[album.Id] = tracks[0];
        }

        var (oldest, oldestTracks) = MakeRelease("Zzz Oldest Megarelease", 1990, 99, "Oldest First");
        artistAlbums.Add(oldest);
        allTracks.AddRange(oldestTracks);
        firstTrackByAlbum[oldest.Id] = oldestTracks[0];

        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(artist, artistAlbums, allTracks, firstTrackByAlbum, queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        // All counted albums tie at 2 tracks / 1995; the deterministic tie-break is Name
        // ascending, so "Album 00" wins. The uncounted 99-track megarelease does not.
        Assert.Equal(firstTrackByAlbum.Values.First(t => t.Name == "Album 00 First").Id.ToString(), token);
        // COUNT-only ParentId queries (all albums well-formed, so no AlbumIds fallback).
        var countQueries = queries.Where(q => q.Limit == 0 && q.ParentId != Guid.Empty).ToList();
        Assert.Equal(12, countQueries.Count);
        Assert.DoesNotContain(countQueries, q => q.ParentId == oldest.Id);
        Assert.DoesNotContain(queries, q => q.Limit == 0 && q.AlbumIds is { Length: > 0 });
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_PromptsDisambiguation()
    {
        // JF-341: distinct-name multi-match (e.g. "Greatest Hits" vs "Biggest Hits") must
        // prompt the user (AskFirstMatch), not silently auto-play the best-scoring one.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        _fx.SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(new List<BaseItem> { album1, album2 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), user, CreateSession(), CancellationToken.None);

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
        _fx.SetupUserMock();

        var album1 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_EmbeddedContainmentFuzzyMatch_DoesNotAutoPlay()
    {
        // JF-408 incident replay (live 2026-08-28): query "walls for cup" (ASR for "Waltz for
        // Koop") fuzzy-matched album "O" at ContainmentScore because the query contains an 'o'
        // inside "for", and auto-played it on-device. The fuzzy match is returned by the recall
        // layer; the auto-play decision must reject an interior-only containment.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "walls for cup");
        _fx.SetupUserMock();

        // Exact search (SearchTerm set) must miss; the fuzzy fallback's full-catalog scan
        // (SearchTerm null) returns the degenerate 1-char album.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "O", Id = Guid.NewGuid() } }
                : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);
    }

    [Fact]
    public async Task HandleAsync_EmbeddedContainmentFuzzyMatch_DeviceCorr_DarkSideOfTheMoon_DoesNotAutoPlay()
    {
        // JF-478 incident replay (live 2026-09-04, corr=80bb4642, it-IT): 'riproduci
        // album dark side of the moon' arrived with the album slot FILLED. The exact
        // search missed, the fuzzy fallback matched Damien Rice's single-letter album
        // 'O' at containment score 90, and the skill auto-played it. The 'o' is
        // word-INITIAL inside "of" (and interior inside "moon"), so the JF-408
        // every-occurrence-strictly-interior rule did not fire; the rejection must
        // cover every embedded-fragment shape, never only the strictly-interior one.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "dark side of the moon", locale: "it-IT");
        _fx.SetupUserMock();

        // Exact search (SearchTerm set) must miss; the fuzzy fallback's full-catalog
        // scan (SearchTerm null) returns the degenerate 1-char album.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "O", Id = Guid.NewGuid() } }
                : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);

        // Clean not-found: 3 content words (dark/side/moon) exceed the cross-media
        // artist guard, so no artist recovery fires either; the spoken words echo the
        // user's own query back.
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("dark side of the moon", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WholeWordContainmentFuzzyMatch_StillAutoPlays()
    {
        // JF-478 no-regression mirror (cascade reference class): a short REAL album
        // name carried inside slot text as a WHOLE WORD ('u2' in the carrier-bleed
        // shape "un disco di u2") must keep auto-playing. Only embedded fragments of
        // other words are rejected; boundary-legit occurrences are not.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "un disco di u2");
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "U2", Id = Guid.NewGuid() } }
                : new List<BaseItem>());
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { new Audio { Name = "One", Id = Guid.NewGuid() } }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_PluralAffixedContainmentFuzzyMatch_StillAutoPlays()
    {
        // JF-478 no-regression mirror (cascade reference class, pinned at the predicate
        // level by IsEmbeddedContainment_PluralAffixedOccurrence_False): the ASR-plural
        // shape "outkasts" -> album "Outkast" is an edge occurrence whose word extends
        // the candidate by one character (a plausible affix form) and keeps playing.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "outkasts");
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "Outkast", Id = Guid.NewGuid() } }
                : new List<BaseItem>());
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { new Audio { Name = "Hey Ya", Id = Guid.NewGuid() } }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
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
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (album, albumTracks) = MakeRelease("Waltz for Koop", 1997, 1, "Baby");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] },
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(albumTracks[0].Id.ToString(), token);

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
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.SetupUserMock();

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
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Queen", Id = Guid.NewGuid() };
        var (album, albumTracks) = MakeRelease("A Night at the Opera", 1975, 1, "Bohemian Rhapsody");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] },
            queries);

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.Config.DefaultAnnounceNowPlaying = true; // video/book toggle on
        _fx.Config.AnnounceAudioPlays = false; // audio opt-in default
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        _fx.SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.Config.AnnounceAudioPlays = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        _fx.SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        _fx.Config.AnnounceAudioPlays = false;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        _fx.SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    // -----------------------------------------------------------------------
    // JF-471: judgment layer on the album-by-artist path. Device incident
    // 2026-09-03 (corr=38498471): "riproduci album dark side of the moon" arrived
    // with album=EMPTY and musician='dark side of the moon' (the JF-469 Amazon
    // slot theft); the search chain's JF-437 word-coverage tier matched the band
    // 'Dark Dark Dark' (name word 'dark' inside the query's {dark, side, moon})
    // and the skill silently played that band's album 'In Your Dreams'.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a ready in-memory artist index over the given artists with REAL
    /// DoubleMetaphone codes (mirroring ArtistIndexService, which computes
    /// DoubleMetaphone.Encode(artist.Name) at build time), so the search chain
    /// runs its genuine tier semantics instead of an invented mock shape.
    /// </summary>
    private static FakeArtistIndex BuildPhoneticArtistIndex(params BaseItem[] artists)
    {
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)>();
        foreach (BaseItem artist in artists)
        {
            codes[artist.Id] = DoubleMetaphone.Encode(artist.Name!);
        }

        return new FakeArtistIndex(artists, codes);
    }

    /// <summary>
    /// The chain-side mechanism, pinned at the unit level: the word-coverage tier
    /// (1.5) returns 'Dark Dark Dark' for 'dark side of the moon' as a SUBSET free
    /// pass (no score bar), the honest fuzzy score is far below the acceptance
    /// threshold, and the phonetic codes do NOT collide. This is why the JF-471 gate
    /// lives at the handler's decision point rather than in ArtistSearch (recall
    /// layer; the tier's subset matches stay reachable for callers WITH downstream
    /// judgment).
    /// </summary>
    [Fact]
    public void ArtistSearch_WordCoverage_FreePassOnStolenAlbumSpan()
    {
        var artist = new MusicArtist { Name = "Dark Dark Dark", Id = Guid.NewGuid() };

        List<BaseItem> coverage = ArtistSearch.WordCoverageCandidates(
            "dark side of the moon", new[] { artist }, "it-IT");

        Assert.Single(coverage);
        Assert.Equal("Dark Dark Dark", coverage[0].Name);
        Assert.True(
            FuzzyMatcher.Score("dark side of the moon", "Dark Dark Dark") < FuzzyMatcher.DefaultThreshold,
            "the honest fuzzy score must be below the acceptance threshold for the free-pass classification");
        var queryCodes = DoubleMetaphone.Encode("dark side of the moon");
        var nameCodes = DoubleMetaphone.Encode("Dark Dark Dark");
        Assert.False(
            FuzzyMatcher.PhoneticCodesMatch(queryCodes.Primary, queryCodes.Alternate, nameCodes.Primary, nameCodes.Alternate),
            "the match must not be a phonetic code collision for the free-pass classification");
    }

    /// <summary>
    /// JF-471 failing-state pin, post-fix assertion: the album-by-artist path must
    /// NOT silently auto-play an unrelated artist's album when the musician slot
    /// carries a stolen album span that only the word-coverage free pass matched.
    /// Pre-fix behavior (probe, 2026-09-03): 'In Your Dreams' auto-played with NO
    /// announcement (fuzzyAlbumAnnouncement is only set on the album-name fuzzy
    /// fallback, and AnnounceAudioPlays defaults off).
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_StolenAlbumSpanFreePassArtist_DoesNotAutoPlay()
    {
        var artist = new MusicArtist { Name = "Dark Dark Dark", Id = Guid.NewGuid() };
        var neighbor = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist, neighbor);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "dark side of the moon");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("In Your Dreams", 2011, 10, "In Your Dreams First");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);
        // Clean not-found in the user's own words (the same string the zero-result
        // path speaks), not a prompt and not a silent substitution.
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("dark side of the moon", TestHelpers.GetSpeechText(response));
    }

    /// <summary>
    /// JF-471 legit-flow pin (byte-identical requirement): a real artist in the
    /// musician slot auto-plays its album exactly as before the gate. The chain
    /// accepts 'pink floyd' at tier 1 (contains) and the gate re-scores it at 100,
    /// so the resolution block is untouched.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_RealArtistMatch_StillAutoPlays()
    {
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "pink floyd");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("The Dark Side of the Moon", 1973, 10, "Speak to Me");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
        // Byte-identical shape: session-ending play, no announcement (AnnounceAudioPlays
        // is off by default and no substitution happened).
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);
    }

    /// <summary>
    /// JF-471 no-regression pin: the JF-381 phonetic accent-drift class ('cup' for
    /// 'Koop', both Double Metaphone KP) must keep auto-playing on the album-by-artist
    /// path. The gate's phonetic arm mirrors the matcher's own acceptance (the
    /// length-banded collision floor), so the flagship feature survives the guard.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_PhoneticAccentDrift_StillAutoPlays()
    {
        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "cup");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("Waltz for Koop", 1997, 10, "Baby");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
    }

    /// <summary>
    /// JF-471 no-regression pin: the JF-437 qualifier-query class ('miles davis
    /// live' -> 'Miles Davis', a word-coverage tier match whose name is CONTAINED in
    /// the query and therefore scores ContainmentScore) must keep auto-playing; the
    /// gate only refuses the below-threshold free pass, not the tier's intended class.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_QualifierQuery_StillAutoPlays()
    {
        var artist = new MusicArtist { Name = "Miles Davis", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "miles davis live");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("Kind of Blue", 1959, 5, "So What");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
    }

    /// <summary>
    /// JF-471 scope pin: the gate lives in the album-by-artist resolution (album slot
    /// EMPTY). When an album TITLE is present, a weak musician match keeps today's
    /// behavior (the artist ids only filter the album-title query); widening the gate
    /// to the album-titled path would change behavior this test does not sanction.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumTitlePresent_WeakMusicianMatch_KeepsTodayBehavior()
    {
        var artist = new MusicArtist { Name = "Dark Dark Dark", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(album: "in your dreams", musician: "dark side of the moon");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("In Your Dreams", 2011, 10, "In Your Dreams First");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    // -----------------------------------------------------------------------
    // JF-473 (JF-377 parity): a single-word artist whose name is genuinely
    // CONTAINED in a stolen album span passes the JF-471 acceptance gate by
    // construction (the containment shortcut scores 90, above the bar), so the
    // album-by-artist path silently auto-played its album. PlayArtistSongs has
    // downgraded exactly this shape to a yes/no prompt since JF-377; these pins
    // hold the same contract at the album-by-artist acceptance point.
    // -----------------------------------------------------------------------

    /// <summary>
    /// JF-473 failing-state pin: the coincidental-containment class that passes
    /// the JF-471 gate (containment 90 by construction) must NOT silently
    /// auto-play the album; it gets the JF-377 yes/no prompt instead. Pre-fix
    /// behavior (probe, 2026-09-04): 'In Your Dreams' by 'Dark' auto-played for
    /// musician='dark side of the moon'.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_SingleWordCoincidentalContainment_PromptsInsteadOfAutoPlay()
    {
        var artist = new MusicArtist { Name = "Dark", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "dark side of the moon", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("In Your Dreams", 2011, 10, "In Your Dreams First");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);
        // The JF-377 prompt contract, not a terminal Tell: the session stays open
        // for the yes/no cycle.
        Assert.False(response.Response.ShouldEndSession ?? false);
        Assert.NotNull(response.SessionAttributes);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes[DisambiguationHelper.AttrType]);
        var matches = JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            response.SessionAttributes[DisambiguationHelper.AttrMatches].ToString());
        Assert.NotNull(matches);
        DisambiguationHelper.MatchInfo match = Assert.Single(matches);
        Assert.Equal("Dark", match.Name);
        Assert.Equal(artist.Id.ToString(), match.Id);
        // The prompt speaks the candidate so the user can confirm or decline it.
        Assert.Contains("Dark", TestHelpers.GetSpeechText(response));
    }

    /// <summary>
    /// JF-473 legit-class pin: a real single-word artist inside a carrier phrase
    /// that bled into the raw slot ('un disco di u2', the JF-377 carrier-bleed
    /// class) is NOT a coincidental containment under the shared predicate (the
    /// name occurs whole-word and covers half the query's content words), so it
    /// keeps auto-playing exactly as before the downgrade.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MusicianOnly_CarrierBearingSingleWordArtist_StillAutoPlays()
    {
        var artist = new MusicArtist { Name = "U2", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "un disco di u2", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("The Joshua Tree", 1987, 10, "Where the Streets Have No Name");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
    }

    /// <summary>
    /// JF-473 yes-route pin: the prompt's session attributes round-trip through
    /// YesIntentHandler's artist disambiguation arm (disambig_type=artist ->
    /// PlayArtist), so a user-confirmed coincidental containment still PLAYS (the
    /// JF-377 contract: the prompt defers judgment to the user, it never
    /// rejects). End-to-end: the attrs fed to the Yes handler are the ones the
    /// PlayAlbum prompt built, not a hand-written copy.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SingleWordCoincidentalContainment_Prompt_YesPlaysTheArtist()
    {
        var artist = new MusicArtist { Name = "Dark", Id = Guid.NewGuid() };
        var index = BuildPhoneticArtistIndex(artist);
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            index);
        var request = CreateIntentRequest(musician: "dark side of the moon", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("In Your Dreams", 2011, 10, "In Your Dreams First");
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] });

        SkillResponse prompt = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        Assert.NotNull(prompt.SessionAttributes);

        // The Yes leg resolves the candidate by id and plays the artist's songs:
        // GetItemById serves the artist, GetItemList serves the PlayArtist query.
        _fx.LibraryManager.Setup(l => l.GetItemById(artist.Id)).Returns(artist);
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(albumTracks);
        var yesHandler = new YesIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);
        var yesRequest = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.YesIntent" },
            Locale = "it-IT",
            RequestId = "test-req"
        };

        SkillResponse confirmed = await yesHandler.HandleAsync(
            yesRequest,
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            prompt.SessionAttributes,
            CancellationToken.None);

        var playDirective = confirmed.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
    }

    // -------------------------------------------------------------------------
    // JF-469: it-IT calling-word slot bleed. The NLU fills the album slot with the
    // literal calling word ('cerca un album chiamato X' -> album "chiamato X"); the
    // handler-side strip is a RAW-FIRST fallback: the stripped retry fires only when
    // the raw-value query missed, so an album actually titled "Chiamato qualcosa"
    // keeps playing through the raw query.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Mocks the album-title search keyed on the SearchTerm value: a query whose
    /// SearchTerm has a map entry returns that entry's albums, every other query
    /// (including the fuzzy full-catalog scan, SearchTerm null) misses. Queries are
    /// recorded into <paramref name="queries"/> for assertions.
    /// </summary>
    private void SetupTitleSearchByTerm(Dictionary<string, List<BaseItem>> resultsBySearchTerm, List<InternalItemsQuery> queries)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                return q.SearchTerm != null && resultsBySearchTerm.TryGetValue(q.SearchTerm, out List<BaseItem>? results)
                    ? results
                    : new List<BaseItem>();
            });
    }

    [Fact]
    public async Task HandleAsync_CallingWordBleed_RawQueryHits_NoStrippedRetry()
    {
        // JF-469 raw-first pin: when the RAW slot value ('chiamato thriller', the
        // profile-nlu fill of 'cerca un album chiamato thriller') finds the album,
        // the calling-word strip must NOT fire: exactly one title query, on the raw
        // value, and the album plays.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "chiamato thriller", locale: "it-IT");
        _fx.SetupUserMock();

        var album = new MusicAlbum { Name = "Chiamato Thriller", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(
            new Dictionary<string, List<BaseItem>> { ["chiamato thriller"] = new() { album } },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(track.Id.ToString(), playDirective!.AudioItem.Stream.Token);

        // One title query only, on the raw value: the stripped variant ('thriller')
        // was never queried (the fallback does not fire when the raw query hits).
        List<string> searchTerms = queries.Where(q => q.SearchTerm != null).Select(q => q.SearchTerm!).ToList();
        Assert.Single(searchTerms);
        Assert.Equal("chiamato thriller", searchTerms[0]);
    }

    [Fact]
    public async Task HandleAsync_CallingWordBleed_RawMiss_StrippedRetryHits_AndPlays()
    {
        // The evidenced live shape (profile-nlu 2026-09-04: 'un album che si chiama
        // surfer rosa' -> album "che si chiama surfer rosa"): the raw query misses,
        // the calling-word-stripped retry finds the album and it plays.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "che si chiama surfer rosa", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, tracks) = MakeRelease("Surfer Rosa", 1988, 2, "Bone Machine");
        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(
            new Dictionary<string, List<BaseItem>>
            {
                ["surfer rosa"] = new() { album }
            },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { tracks[0] }, TotalRecordCount = tracks.Count });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(tracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);

        // The stripped retry fired, AFTER the raw query (raw-first order).
        List<string> searchTerms = queries.Where(q => q.SearchTerm != null).Select(q => q.SearchTerm!).ToList();
        Assert.Equal(2, searchTerms.Count);
        Assert.Equal("che si chiama surfer rosa", searchTerms[0]);
        Assert.Equal("surfer rosa", searchTerms[1]);
    }

    [Fact]
    public async Task HandleAsync_CallingWordBleed_RawAndStrippedMiss_CleanNotFoundNamesRawValue()
    {
        // Both the raw and the stripped query miss: the not-found speech names the
        // RAW slot value (the user said "chiamato X"; the not-found names what they
        // said), and nothing plays.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "chiamato xyzzyfoo", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(new Dictionary<string, List<BaseItem>>(), queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("chiamato xyzzyfoo", speech, StringComparison.OrdinalIgnoreCase);

        // The stripped retry fired exactly once (raw first, then 'xyzzyfoo'), then
        // the fuzzy scan and the artist fallback ran their own queries.
        List<string> searchTerms = queries.Where(q => q.SearchTerm != null).Select(q => q.SearchTerm!).ToList();
        Assert.Equal("chiamato xyzzyfoo", searchTerms[0]);
        Assert.Contains("xyzzyfoo", searchTerms);
    }

    [Fact]
    public async Task HandleAsync_AlbumLiterallyTitledCallingWord_RawQueryFindsIt_NoStripInterference()
    {
        // CRITICAL JF-469 safety: an album ACTUALLY TITLED with a leading calling
        // word ("Chiamato Moe") must stay findable through the RAW query. The strip
        // is a fallback on a raw miss, so the raw hit path never rewrites the query
        // and the album titled with the calling word plays.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "chiamato moe", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, tracks) = MakeRelease("Chiamato Moe", 2001, 3, "Moe First");
        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(
            new Dictionary<string, List<BaseItem>> { ["chiamato moe"] = new() { album } },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { tracks[0] }, TotalRecordCount = tracks.Count });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(tracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);

        // No 'moe'-only query was ever issued: the raw query found the album.
        Assert.DoesNotContain(queries, q => q.SearchTerm == "moe");
    }

    [Fact]
    public async Task HandleAsync_NoCallingWord_Miss_NoStrippedQuery()
    {
        // A plain title miss (no calling word in the value) must not change shape:
        // every title query carries the raw value; the clean not-found names it.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "thriller", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(new Dictionary<string, List<BaseItem>>(), queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        Assert.Contains("thriller", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.All(
            queries.Where(q => q.SearchTerm != null).Select(q => q.SearchTerm),
            term => Assert.Equal("thriller", term));
    }

    // -------------------------------------------------------------------------
    // JF-489: it-IT calling-word bleed into the MUSICIAN slot. Device
    // 2026-09-04 (corr=f919db65): 'cerca un album chiamato surfer rosa' arrived
    // as album=EMPTY, musician='chiamato surfer rosa' (the calling word AND the
    // title both in the musician slot; a different theft shape than JF-469's
    // album-slot bleed, so the JF-469 album-path strip never fires). Before the
    // artist search the calling word is stripped and the remainder is tried ONCE
    // as an album title; a hit plays the album, a miss searches the artist with
    // the STRIPPED value and every not-found names it.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_MusicianCallingWordBleed_AlbumEmpty_StrippedTitleRetry_PlaysAlbum()
    {
        // The evidenced device shape: album=EMPTY, musician='chiamato surfer rosa'.
        // The stripped album-title retry ('surfer rosa') finds the album and it
        // plays; the artist search never runs (the user asked for an album BY
        // TITLE, and 'chiamato surfer rosa' is garbage as an artist query).
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "chiamato surfer rosa", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, tracks) = MakeRelease("Surfer Rosa", 1988, 2, "Bone Machine");
        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(
            new Dictionary<string, List<BaseItem>> { ["surfer rosa"] = new() { album } },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { tracks[0] }, TotalRecordCount = tracks.Count });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(tracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);

        // Exactly ONE title query (the stripped retry) and no artist query at all:
        // the retry IS the album-title query (no re-query below), and the hit keeps
        // the artist search from ever firing.
        List<string> searchTerms = queries.Where(q => q.SearchTerm != null).Select(q => q.SearchTerm!).ToList();
        Assert.Single(searchTerms);
        Assert.Equal("surfer rosa", searchTerms[0]);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task HandleAsync_MusicianNoCallingWord_AlbumByArtistPath_ByteIdentical()
    {
        // Byte-identical pin: a musician value with NO calling-word prefix keeps
        // the existing album-by-artist path exactly as before JF-489. The retry
        // never fires (no SearchTerm query is ever issued), the artist search and
        // the artist-album resolution run their existing query set, and the album
        // plays (same shape as the JF-471 legit-flow pin, here in it-IT where the
        // calling-word predicate is ACTIVE so the miss of the prefix is meaningful).
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var handler = CreateHandler(BuildPhoneticArtistIndex(artist));
        var request = CreateIntentRequest(musician: "pink floyd", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, albumTracks) = MakeRelease("The Dark Side of the Moon", 1973, 10, "Speak to Me");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] },
            queries);

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(albumTracks[0].Id.ToString(), playDirective!.AudioItem.Stream.Token);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);

        // The JF-489 retry never fired: no album-title query was ever issued. The
        // existing query set is the artist resolution's (in-memory search) plus the
        // artist-album query, none of which carries a SearchTerm.
        Assert.DoesNotContain(queries, q => q.SearchTerm != null);
    }

    [Fact]
    public async Task HandleAsync_MusicianCallingWordBleed_NothingFound_NotFoundNamesStrippedValue()
    {
        // Calling word present, nothing found anywhere: the stripped album-title
        // retry misses, the artist search (also on the stripped value) misses, and
        // the clean not-found names 'xyzzyfoo', NEVER the raw 'chiamato xyzzyfoo'.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "chiamato xyzzyfoo", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupTitleSearchByTerm(new Dictionary<string, List<BaseItem>>(), queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("xyzzyfoo", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chiamato", speech, StringComparison.OrdinalIgnoreCase);

        // The retry ran FIRST on the stripped value (the first recorded query is an
        // album-title query for 'xyzzyfoo', before any artist query), and no query
        // ever carried the raw 'chiamato xyzzyfoo'.
        InternalItemsQuery firstSearch = queries.First(q => q.SearchTerm != null);
        Assert.Equal("xyzzyfoo", firstSearch.SearchTerm);
        Assert.Contains(BaseItemKind.MusicAlbum, firstSearch.IncludeItemTypes ?? Array.Empty<BaseItemKind>());
        Assert.DoesNotContain(queries, q => q.SearchTerm == "chiamato xyzzyfoo");
    }

    // -------------------------------------------------------------------------
    // JF-492: the chiamato-family fill shape WITHOUT the calling word. Device
    // 2026-09-05 (corr=7a54cdf1): 'cerca un album chiamato surfer rosa' arrived as
    // album=EMPTY, musician='surfer rosa' (Amazon's statistical fill consumed
    // 'chiamato' entirely this time; the fill shape drifts BETWEEN requests, so the
    // JF-489 prefix-strip guard does not fire for this shape). The album-by-artist
    // path searched artist 'surfer rosa', found zero, and dead-ended with
    // NotFoundAlbumByArtist. When the artist search returns ZERO artists and the
    // album slot is empty, the searched value (post any calling-word strip) is
    // retried ONCE as an album title before the not-found speech; strictly the
    // artist-MISS case, every artist-hit flow is untouched.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Mocks GetItemList keyed on item type AND SearchTerm: MusicArtist queries
    /// always miss (the artist-miss precondition), MusicAlbum title queries hit only
    /// for mapped titles. SetupTitleSearchByTerm cannot express this shape: it
    /// ignores the item type, so a mapped title would also come back from the artist
    /// search's tier-1 query as a bogus "artist" and the miss would never happen.
    /// Queries are recorded for assertions.
    /// </summary>
    private void SetupArtistMissCatalog(Dictionary<string, List<BaseItem>> albumsByTitle, List<InternalItemsQuery> queries)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true
                    && q.SearchTerm != null
                    && albumsByTitle.TryGetValue(q.SearchTerm, out List<BaseItem>? results))
                {
                    return results;
                }

                return new List<BaseItem>();
            });
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_ArtistMiss_EmptyAlbum_TitleRetryHits_PlaysAlbum()
    {
        // The evidenced device shape (corr=7a54cdf1): album=EMPTY, musician='surfer
        // rosa' with NO calling-word prefix. The artist search misses, the JF-492
        // artist-miss album-title retry finds 'Surfer Rosa', and it plays.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "surfer rosa", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, tracks) = MakeRelease("Surfer Rosa", 1988, 2, "Bone Machine");
        var queries = new List<InternalItemsQuery>();
        SetupArtistMissCatalog(
            new Dictionary<string, List<BaseItem>> { ["surfer rosa"] = new() { album } },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { tracks[0] }, TotalRecordCount = tracks.Count });

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(tracks[0].Id.ToString(), token);

        // The retry is ONE MusicAlbum title query on the searched value, issued only
        // after the artist search had missed (the first recorded query is the artist
        // tier's), and its results feed the play path with no re-query.
        List<InternalItemsQuery> titleQueries = queries.Where(
            q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == "surfer rosa").ToList();
        Assert.Single(titleQueries);
        Assert.Contains(BaseItemKind.MusicArtist, queries[0].IncludeItemTypes ?? Array.Empty<BaseItemKind>());
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_ArtistMiss_TitleRetryMisses_NotFoundNamesSearchedValue()
    {
        // Both the artist search and the artist-miss title retry miss: the not-found
        // keeps today's NotFoundAlbumByArtist shape, naming the SEARCHED value.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "xyzzyfoo", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupArtistMissCatalog(new Dictionary<string, List<BaseItem>>(), queries);

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        Assert.Contains("xyzzyfoo", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);

        // The retry fired exactly once (one MusicAlbum title query on the searched
        // value), after the artist search and before the not-found speech.
        List<InternalItemsQuery> titleQueries = queries.Where(
            q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == "xyzzyfoo").ToList();
        Assert.Single(titleQueries);
        Assert.Contains(BaseItemKind.MusicArtist, queries[0].IncludeItemTypes ?? Array.Empty<BaseItemKind>());
    }

    [Fact]
    public async Task HandleAsync_MusicianCallingWord_BothMiss_NoSecondTitleRetryQuery()
    {
        // Calling-word musician + both misses: the JF-489 strip retry already ran an
        // album-TITLE query for the stripped value and missed, so the JF-492
        // artist-miss retry must NOT re-issue the identical query; exactly one
        // title query total, and the not-found names the stripped value
        // (code-review 2026-09-05: the double query was one redundant bounded
        // query inside the Alexa window).
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "chiamato xyzzyfoo", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupArtistMissCatalog(new Dictionary<string, List<BaseItem>>(), queries);

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("xyzzyfoo", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chiamato", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);

        // Exactly ONE album-title query for the stripped value (the JF-489 one):
        // the JF-492 retry skipped it because it already missed.
        List<InternalItemsQuery> strippedTitleQueries = queries.Where(
            q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == "xyzzyfoo").ToList();
        Assert.Single(strippedTitleQueries);
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_ArtistHit_NoAlbumTitleRetryQuery()
    {
        // Artist-hit pin: the JF-492 retry fires ONLY on the artist miss. A found
        // artist keeps the JF-411 album-by-artist resolution exactly as before, with
        // no album-title query ever issued.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop", locale: "it-IT");
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var (album, albumTracks) = MakeRelease("Waltz for Koop", 1997, 10, "Baby");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            albumTracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = albumTracks[0] },
            queries);

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(albumTracks[0].Id.ToString(), token);

        // No ALBUM-TITLE query was ever issued (the artist search's own SearchTerm
        // tiers are MusicArtist queries and keep running, the JF-492 retry fires only
        // on the artist miss).
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm != null);
    }

    [Fact]
    public async Task HandleAsync_MusicianCallingWordBleed_TitleRetryHits_NoSecondAlbumTitleQuery()
    {
        // JF-489/JF-492 interaction pin: on the JF-489 calling-word HIT path the
        // stripped-title retry's results already feed the play path and the musician
        // is cleared, so the artist search never runs and the JF-492 post-miss retry
        // cannot fire: exactly ONE album-title query in total.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "chiamato surfer rosa", locale: "it-IT");
        _fx.SetupUserMock();

        var (album, tracks) = MakeRelease("Surfer Rosa", 1988, 2, "Bone Machine");
        var queries = new List<InternalItemsQuery>();
        SetupArtistMissCatalog(
            new Dictionary<string, List<BaseItem>> { ["surfer rosa"] = new() { album } },
            queries);
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { tracks[0] }, TotalRecordCount = tracks.Count });

        string token = await GetPlayedTrackTokenAsync(handler, request, CreateSession());

        Assert.Equal(tracks[0].Id.ToString(), token);

        // Times verification: exactly one album-title query (the JF-489 stripped
        // retry) and zero artist queries.
        int titleQueryCount = queries.Count(
            q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm != null);
        Assert.Equal(1, titleQueryCount);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task HandleAsync_ArtistMiss_AlbumTitlePresent_NoTitleRetry_NotFoundStaysByArtist()
    {
        // Scope pin: the JF-492 retry is scoped to the musician-ONLY fill shape. When
        // an album title is already present and the artist search misses, today's
        // NotFoundAlbumByArtist stands and the musician value is never re-queried as
        // a title.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "thriller", musician: "surfer rosa", locale: "it-IT");
        _fx.SetupUserMock();

        var queries = new List<InternalItemsQuery>();
        SetupArtistMissCatalog(new Dictionary<string, List<BaseItem>>(), queries);

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        Assert.Contains("surfer rosa", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);

        // No album-title query was ever issued: the artist-filtered not-found returns
        // before the album-title path, and the retry did not fire.
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm != null);
    }

    /// <summary>
    /// Test-only subclass exposing the protected JF-469 strip predicate for direct
    /// unit testing (the TestBaseHandler precedent).
    /// </summary>
    private class TestStripHandler : BaseHandler
    {
        public TestStripHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => false;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(new SkillResponse());

        public static bool CallTryStripLeadingAlbumCallingWord(string? slotValue, string locale, out string stripped)
            => TryStripLeadingAlbumCallingWord(slotValue, locale, out stripped);
    }

    [Theory]
    [InlineData("chiamato thriller", "thriller")]
    [InlineData("chiamata canzone", "canzone")]
    [InlineData("che si chiama surfer rosa", "surfer rosa")]
    [InlineData("di nome thriller", "thriller")]
    [InlineData("Chiamato Thriller", "Thriller")]
    public void TryStripLeadingAlbumCallingWord_ItSlots_CallingWordStripped(string raw, string expected)
    {
        Assert.True(TestStripHandler.CallTryStripLeadingAlbumCallingWord(raw, "it-IT", out string stripped));
        Assert.Equal(expected, stripped);
    }

    [Theory]
    [InlineData("chiamato")]
    [InlineData("chiamato ")]
    [InlineData("")]
    [InlineData("  ")]
    public void TryStripLeadingAlbumCallingWord_BareOrEmpty_NeverStrips(string raw)
    {
        // A slot that IS the calling word has no title left once stripped: the raw
        // value must be kept (an album literally titled "Chiamato" stays searchable).
        Assert.False(TestStripHandler.CallTryStripLeadingAlbumCallingWord(raw, "it-IT", out _));
    }

    [Fact]
    public void TryStripLeadingAlbumCallingWord_WordFragmentPrefix_NeverStrips()
    {
        // The trailing space in every map entry means a strip can never cut a word
        // fragment: "chiamatole qualcosa" (a title starting with the letters of
        // "chiamato") is not the calling word.
        Assert.False(TestStripHandler.CallTryStripLeadingAlbumCallingWord("chiamatole qualcosa", "it-IT", out _));
    }

    [Theory]
    [InlineData("chiamato thriller", "en-US")]
    [InlineData("chiamato thriller", "en-GB")]
    [InlineData("called thriller", "it-IT")]
    [InlineData("llamada thriller", "it-IT")]
    public void TryStripLeadingAlbumCallingWord_OtherLocalesOrWords_NeverStrips(string raw, string locale)
    {
        // Scope pin: it-IT only, and only the it-IT calling words. The other locales
        // carry no album-path calling-word samples (JF-469 survey) and their fills
        // are clean, so their values are never rewritten.
        Assert.False(TestStripHandler.CallTryStripLeadingAlbumCallingWord(raw, locale, out _));
    }
}
