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
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;

    public PlayAlbumIntentHandlerTests()
    {
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "playalbum-tests");
    }

    private PlayAlbumIntentHandler CreateHandler()
    {
        return new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
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

    /// <summary>
    /// Mocks the JF-411 indefinite album-by-artist flow: artist lookup, the artist's albums in the
    /// given (insertion) order, per-album track counts for the JF-443 COUNT queries, and
    /// per-album playback results. The count semantics mirror the TWO server mechanisms
    /// (Jellyfin BaseItemRepository 10.11.8/10.11.11): ParentId answers by entity link
    /// (well-formed albums), AlbumIds answers by matching the track's RAW Album tag against
    /// the album entity's Name (f.Name == e.Album; the JF-338 malformed-folder shape).
    /// Queries are recorded into <paramref name="queries"/> (when given) for assertions.
    /// </summary>
    private void SetupIndefiniteAlbumCatalog(
        BaseItem artist,
        List<BaseItem> artistAlbums,
        List<BaseItem> allTracks,
        IReadOnlyDictionary<Guid, BaseItem> firstTrackByAlbumId,
        List<InternalItemsQuery>? queries = null)
    {
        var albumNameById = artistAlbums.ToDictionary(a => a.Id, a => a.Name);

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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

                return new List<BaseItem>();
            });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                // JF-443 count queries are COUNT-only (Limit=0): the ParentId primary
                // counts by entity link, the AlbumIds fallback by raw-tag name match.
                if (q.Limit == 0)
                {
                    int count = q.ParentId != Guid.Empty
                        ? allTracks.Count(t => t.ParentId == q.ParentId)
                        : allTracks.Count(t => q.AlbumIds is { Length: > 0 }
                            && albumNameById.TryGetValue(q.AlbumIds[0], out string? albumName)
                            && string.Equals(t.Album, albumName, StringComparison.Ordinal));
                    return new QueryResult<BaseItem>
                    {
                        Items = new List<BaseItem>(),
                        TotalRecordCount = count
                    };
                }

                // Playback page queries (nonzero Limit): ParentId first, then the JF-338
                // AlbumIds retry when the folder link finds nothing.
                Guid playKey = q.ParentId != Guid.Empty
                    ? q.ParentId
                    : q.AlbumIds is { Length: > 0 } ? q.AlbumIds[0] : Guid.Empty;
                return firstTrackByAlbumId.TryGetValue(playKey, out BaseItem? track)
                    ? new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 };
            });
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
        SetupIndefiniteAlbumCatalog(
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
        SetupIndefiniteAlbumCatalog(
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
        SetupIndefiniteAlbumCatalog(artist, artistAlbums, allTracks, firstTrackByAlbum, queries);

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
    public async Task HandleAsync_InteriorContainmentFuzzyMatch_DoesNotAutoPlay()
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
        var album = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Baby", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                return new List<BaseItem> { album };
            });
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

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
        SetupIndefiniteAlbumCatalog(
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
}
