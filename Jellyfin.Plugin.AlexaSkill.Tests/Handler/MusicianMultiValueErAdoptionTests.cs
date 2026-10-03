using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-702: the JF-690 multi-value-ER arbitration gate adopted by the remaining
/// musician-slot call sites. Multi-value ER is a property of the catalog-backed
/// JellyfinArtist slot TYPE, so the JF-684 shared-first-word shape (raw "pink"
/// resolving to [P!nk, Pink Floyd]) recurs on every intent whose musician slot
/// drives an artist search; these suites pin the per-site ask legs, the
/// single-resolved survivor legs, the constraint-preservation boundaries (a
/// REAL song/album title keeps today's rank-#1 scoped search), the untouched
/// non-musician flows, the FindSong coexistence shape (the ask supersedes the
/// FindSong flow state, so the yes routes to YesIntentHandler, not the
/// force-route), and the PlayArtistSongs pool-sharing closure (the gate's
/// pool fetch seeds the JF-420 gate instead of a second materialization).
/// </summary>
[Collection("Plugin")]
public class MusicianMultiValueErAdoptionTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new();

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end.
    private readonly DeviceQueueManager _albumQueueManager = TestHelpers.CreateDeviceQueueManager("Jf702AdoptionTests");

    private BaseItem Pnk() => new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
    private BaseItem PinkFloyd() => new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
    private BaseItem Abba() => new MusicArtist { Name = "ABBA", Id = Guid.NewGuid() };

    private static IArtistIndex IndexOf(params BaseItem[] artists)
        => new FakeArtistIndex(artists, FakeArtistIndex.CodesFromArtistNames(artists));

    // ---------------------------------------------------------------
    // Handler factories (each mirrors its suite's construction)
    // ---------------------------------------------------------------

    private AddToQueueIntentHandler CreateQueueHandler(IArtistIndex? index)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory,
            index);

    private PlayNextIntentHandler CreatePlayNextHandler(IArtistIndex? index)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory,
            index);

    private PlayAlbumIntentHandler CreateAlbumHandler(IArtistIndex? index)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _albumQueueManager,
            index);

    private QueryArtistLibraryIntentHandler CreateQueryHandler(IArtistIndex? index)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private FindSongIntentHandler CreateFindSongHandler(IArtistIndex? index)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: index);

    // ---------------------------------------------------------------
    // Request builders
    // ---------------------------------------------------------------

    private static IntentRequest CreateQueueIntent(string song, Slot? musicianSlot, string intentName)
    {
        var intent = new Intent { Name = intentName };
        // The twins read the musician slot via the dictionary indexer, so the
        // slot must exist even when absent of value (the production wire shape:
        // Alexa echoes every declared slot; the QueueIntentHandlerTests pattern).
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song },
            ["musician"] = musicianSlot ?? new Slot { Name = "musician" }
        };

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static IntentRequest CreateAlbumIntent(string? album, Slot? musicianSlot)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>();
        if (album != null)
        {
            intent.Slots["album"] = new Slot { Name = "album", Value = album };
        }

        if (musicianSlot != null)
        {
            intent.Slots["musician"] = musicianSlot;
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static IntentRequest CreateQueryIntent(Slot musicianSlot, string? queryType = null)
    {
        var intent = new Intent { Name = IntentNames.QueryArtistLibrary };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = musicianSlot };
        if (queryType != null)
        {
            intent.Slots["query_type"] = new Slot { Name = "query_type", Value = queryType };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static IntentRequest CreateFindSongIntent(Slot? musicianSlot, string? titleKeywords = null)
    {
        var intent = new Intent { Name = IntentNames.FindSongIntent };
        intent.Slots = new Dictionary<string, Slot>();
        if (musicianSlot != null)
        {
            intent.Slots["musician"] = musicianSlot;
        }

        if (titleKeywords != null)
        {
            intent.Slots["titleKeywords"] = new Slot { Name = "titleKeywords", Value = titleKeywords };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    // ---------------------------------------------------------------
    // Shared assertions (the JF-690 suite's shapes; the ask and no-state
    // oracles live on TestHelpers since the JF-707 gate-marker rework)
    // ---------------------------------------------------------------

    /// <summary>
    /// Library mock for the queue twins: the SONG title search (SearchTerm set,
    /// scoped or not; an unscoped query carries an EMPTY ArtistIds array, not
    /// null) returns the given songs, and the artist search's database tiers
    /// (MusicArtist kind) return the given artists so a fall-through search
    /// resolves. Recorded queries let the pins assert WHICH artist scoped the
    /// song search.
    /// </summary>
    private void SetupQueueLibrary(List<InternalItemsQuery> queries, List<BaseItem> artists, params Audio[] songs)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem>(artists);
                }

                if (q.SearchTerm != null)
                {
                    return new List<BaseItem>(songs);
                }

                return new List<BaseItem>();
            });
    }

    private static FindSongSessionData? ReadFindSongState(SkillResponse response)
    {
        string? json = response.SessionAttributes?.GetValueOrDefault(FindSongIntentHandler.SessionDataKey)?.ToString();
        return json == null ? null : JsonConvert.DeserializeObject<FindSongSessionData>(json);
    }

    private static Dictionary<string, object> FindSongSession(FindSongState state, string? keywords = null)
        => new()
        {
            [FindSongIntentHandler.SessionDataKey] = JsonConvert.SerializeObject(
                new FindSongSessionData { State = state, Keywords = keywords })
        };

    // ===============================================================
    // AddToQueue
    // ===============================================================

    [Fact]
    public async Task AddToQueue_GenericSongWord_MultiValueEr_AsksWhichArtist()
    {
        // "metti in coda la musica di pink": the song slot carries the JF-697
        // generic music word, so the gate asks which artist instead of silently
        // scoping the add to Amazon's rank #1.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>());

        var handler = CreateQueueHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task AddToQueue_RealSongTitle_MultiValueEr_NoAsk_ScopesTitleSearchToRank1()
    {
        // A REAL song title keeps today's path (the PlaySong scope restriction):
        // the confirm leg cannot preserve the requested song, so the title search
        // runs scoped to Amazon's rank #1 artist as before.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Just Like a Pill", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem> { pnk, floyd }, song);

        var handler = CreateQueueHandler(IndexOf(pnk, floyd));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("just like a pill", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        // EVERY title query is scoped to rank #1 ONLY (gate-marker tail
        // strengthening: a regression that scopes to both matched artists, or
        // that issues an extra unscoped query alongside, must FAIL, not
        // coexist with a passing Contains; floyd is in the library so the
        // multi-artist shape is representable).
        var titleQueries = queries.Where(q => q.SearchTerm != null).ToList();
        Assert.NotEmpty(titleQueries);
        Assert.All(titleQueries, q => Assert.True(
            q.ArtistIds is { Length: 1 } && q.ArtistIds[0] == pnk.Id,
            $"title query was not rank-#1-only scoped: {q.SearchTerm}"));
    }

    [Fact]
    public async Task AddToQueue_GenericSongWord_Collapse_ScopesTheAddToTheSurvivor()
    {
        // The stale-catalog collapse: rank #1 left the library, only ABBA is
        // real. The add proceeds with the proven survivor as the artist scope
        // (never the stale rank-#1 canonical).
        var abba = Abba();
        var song = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>(), song);

        var handler = CreateQueueHandler(IndexOf(abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA"), IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        // The survivor scoped the song search and no artist-search query ran
        // (the collapse supplies the scope without the chain).
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == abba.Id);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task AddToQueue_NoMusician_SongOnly_Unchanged()
    {
        // The non-musician flow is untouched: no musician slot, the song search
        // runs unscoped exactly as before the adoption.
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>(), song);

        var handler = CreateQueueHandler(IndexOf(Pnk(), PinkFloyd()));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("money", musicianSlot: null, IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Contains(queries, q => q.SearchTerm == "money" && (q.ArtistIds == null || q.ArtistIds.Length == 0));
    }

    // ===============================================================
    // PlayNext (the AddToQueue twin)
    // ===============================================================

    [Fact]
    public async Task PlayNext_GenericSongWord_MultiValueEr_AsksWhichArtist()
    {
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>());

        var handler = CreatePlayNextHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), IntentNames.PlayNext),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task PlayNext_RealSongTitle_MultiValueEr_NoAsk_ScopesTitleSearchToRank1()
    {
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Get the Party Started", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem> { pnk, floyd }, song);

        var handler = CreatePlayNextHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("get the party started", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), IntentNames.PlayNext),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        var pnTitleQueries = queries.Where(q => q.SearchTerm != null).ToList();
        Assert.NotEmpty(pnTitleQueries);
        Assert.All(pnTitleQueries, q => Assert.True(
            q.ArtistIds is { Length: 1 } && q.ArtistIds[0] == pnk.Id,
            $"title query was not rank-#1-only scoped: {q.SearchTerm}"));
    }

    [Fact]
    public async Task PlayNext_GenericSongWord_Collapse_ScopesTheInsertToTheSurvivor()
    {
        var abba = Abba();
        var song = new Audio { Name = "Fernando", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>(), song);

        var handler = CreatePlayNextHandler(IndexOf(abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA"), IntentNames.PlayNext),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == abba.Id);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task PlayNext_NoMusician_SongOnly_Unchanged()
    {
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>(), song);

        var handler = CreatePlayNextHandler(IndexOf(Pnk(), PinkFloyd()));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("money", musicianSlot: null, IntentNames.PlayNext),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Contains(queries, q => q.SearchTerm == "money" && (q.ArtistIds == null || q.ArtistIds.Length == 0));
    }

    // ===============================================================
    // PlayAlbum
    // ===============================================================

    [Fact]
    public async Task PlayAlbum_MusicianOnly_MultiValueEr_AsksWhichArtist()
    {
        // "un disco di pink" / "gli album di pink" (the JF-690 filing's certain
        // recurrence example): the JF-411 album-by-artist shape has no album
        // constraint to preserve, so the gate asks which artist.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateAlbumHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateAlbumIntent(album: null, TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task PlayAlbum_AlbumTitlePresent_MultiValueEr_NoAsk()
    {
        // An album TITLE in hand keeps today's path: the confirm leg cannot
        // preserve the requested album, so the artist search runs on Amazon's
        // rank #1 as before and the album path proceeds.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var (album, tracks) = MakeAlbumRelease("Greatest Hits", 1995, 3, "First Hit");
        _fx.SetupUserMock();
        _fx.SetupIndefiniteAlbumCatalog(
            pnk,
            new List<BaseItem> { album },
            tracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = tracks[0] });

        var handler = CreateAlbumHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateAlbumIntent("greatest hits", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
    }

    [Fact]
    public async Task PlayAlbum_MusicianOnly_Collapse_PlaysTheSurvivorsAlbum()
    {
        // The collapse survivor feeds the JF-411 album-by-artist resolution. The
        // JF-471/JF-473 re-judgment gates must SKIP it (arbitrationResolvedArtist):
        // re-scoring the survivor ("ABBA") against the stale rank-#1 canonical
        // ("P!nk") would refuse the play that proven evidence earned. Without the
        // skip this pin fails with NotFoundAlbumByArtist and no play directive.
        var abba = Abba();
        var (album, tracks) = MakeAlbumRelease("Arrival", 1976, 2, "Dancing Queen");
        _fx.SetupUserMock();
        _fx.SetupIndefiniteAlbumCatalog(
            abba,
            new List<BaseItem> { album },
            tracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = tracks[0] });

        var handler = CreateAlbumHandler(IndexOf(abba));
        SkillResponse response = await handler.HandleAsync(
            CreateAlbumIntent(album: null, TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        var play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(tracks[0].Id.ToString(), play!.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task PlayAlbum_AlbumOnly_NoMusician_Unchanged()
    {
        // The non-musician flow is untouched: an album title alone plays through
        // the album-title path exactly as before the adoption.
        var (album, tracks) = MakeAlbumRelease("Arrival", 1976, 2, "Dancing Queen");
        _fx.SetupUserMock();
        _fx.SetupIndefiniteAlbumCatalog(
            Pnk(),
            new List<BaseItem> { album },
            tracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = tracks[0] });

        var handler = CreateAlbumHandler(IndexOf(Pnk(), PinkFloyd()));
        SkillResponse response = await handler.HandleAsync(
            CreateAlbumIntent("arrival", musicianSlot: null),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
    }

    private static (MusicAlbum Album, List<BaseItem> Tracks) MakeAlbumRelease(string name, int year, int trackCount, string firstTrackName)
    {
        var albumItem = new MusicAlbum { Name = name, Id = Guid.NewGuid(), ProductionYear = year };
        var releaseTracks = new List<BaseItem>();
        for (int i = 0; i < trackCount; i++)
        {
            releaseTracks.Add(new Audio
            {
                Name = i == 0 ? firstTrackName : $"{name} track {i + 1}",
                Id = Guid.NewGuid(),
                Album = name,
                ParentId = albumItem.Id
            });
        }

        return (albumItem, releaseTracks);
    }

    // ===============================================================
    // QueryArtistLibrary
    // ===============================================================

    [Fact]
    public async Task QueryArtistLibrary_MultiValueEr_AsksWhichArtist()
    {
        // "cosa abbiamo di pink": this intent's only content input is the
        // musician slot, so the gate runs unrestricted.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateQueryHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueryIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task QueryArtistLibrary_Collapse_ListsTheSurvivor()
    {
        // The collapse survivor is listed directly: the tracks query carries the
        // survivor's id (never the stale rank-#1 canonical's search).
        var abba = Abba();
        var track = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                return q.ArtistIds != null && q.ArtistIds.Length > 0
                    ? new List<BaseItem> { track }
                    : new List<BaseItem>();
            });

        var handler = CreateQueryHandler(IndexOf(abba));
        SkillResponse response = await handler.HandleAsync(
            CreateQueryIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(track.Name, speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == abba.Id);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task QueryArtistLibrary_SingleValueEr_Unchanged()
    {
        // The ordinary single-value ER shape is byte-identical to before: the
        // canonical drives the search and the listing answers.
        var floyd = PinkFloyd();
        var track = new Audio { Name = "Money", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.ArtistIds != null && q.ArtistIds.Length > 0
                ? new List<BaseItem> { track }
                : new List<BaseItem>());

        var handler = CreateQueryHandler(IndexOf(floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueryIntent(TestHelpers.ResolvedSlot("pink floyd", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.Contains("Money", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    // ===============================================================
    // FindSong: the first-turn leg (no session state, no interplay)
    // ===============================================================

    [Fact]
    public async Task FindSong_FirstTurn_MultiValueEr_Asks_NoFindSongStateOpened()
    {
        // A FIRST-turn FindSong request carries no FindSongSessionData, so the
        // JF-690 deferral's force-route interplay cannot occur: the ask is the
        // first session state written and the plain JF-420.2 machinery owns the
        // next turn. The FindSong flow simply never opens.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
        Assert.Null(response.SessionAttributes!.GetValueOrDefault(FindSongIntentHandler.SessionDataKey));
    }

    [Fact]
    public async Task FindSong_FirstTurn_Collapse_ResolvesTheSurvivorIntoTheFlow()
    {
        // The collapse resolves the survivor INTO the FindSong flow: the keywords
        // elicit follows with a proven ArtistId instead of a search on the stale
        // rank-#1 canonical.
        var abba = Abba();
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(IndexOf(abba));
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "the keywords elicit keeps the session open");
        TestHelpers.AssertNoDisambiguationState(response);
        FindSongSessionData? state = ReadFindSongState(response);
        Assert.NotNull(state);
        Assert.Equal(FindSongState.AwaitingKeywords, state!.State);
        Assert.Equal(abba.Id, state.ArtistId);
        Assert.Equal("ABBA", state.ArtistName);
    }

    [Fact]
    public async Task FindSong_FirstTurn_BothSlots_MultiValueEr_NoAsk_KeywordsElicitPreserved()
    {
        // The JF-702 code-review restriction: a titleKeywords value in the SAME
        // utterance is in-hand content the gate's confirm leg cannot preserve,
        // so the both-slots shape keeps today's rank-#1 resolution exactly (no
        // ask; the artist resolves and the keywords elicit follows as before).
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), titleKeywords: "una canzone"),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        TestHelpers.AssertSessionOpen(response, "the keywords elicit keeps the session open");
        FindSongSessionData? state = ReadFindSongState(response);
        Assert.NotNull(state);
        Assert.Equal(FindSongState.AwaitingKeywords, state!.State);
        Assert.Equal(pnk.Id, state.ArtistId);
    }

    // ===============================================================
    // FindSong: the AwaitingArtist leg (the coexistence shape)
    // ===============================================================

    [Fact]
    public async Task FindSong_AwaitingArtist_MusicianMultiValue_Asks()
    {
        // A musician-slot-bearing request arriving while the flow awaits the
        // artist (a re-invocation or a force-routed sibling): the gate asks.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(),
            FindSongSession(FindSongState.AwaitingArtist, keywords: "wish you were here"),
            CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task FindSong_AwaitingArtist_Collapse_ProceedsToTheSongSearch()
    {
        // The collapse resolves the survivor into the flow and the artist-scoped
        // song search proceeds with the stored keywords.
        var abba = Abba();
        var song = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                return q.ArtistIds != null && q.ArtistIds.Length > 0
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>();
            });

        var handler = CreateFindSongHandler(IndexOf(abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), session,
            FindSongSession(FindSongState.AwaitingArtist, keywords: "dancing queen"),
            CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == abba.Id);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true);
    }

    [Fact]
    public async Task FindSong_AwaitingArtist_TranscriptAnswer_Unchanged()
    {
        // The elicited-answer leg (the artist captured into titleKeywords, a
        // SearchQuery slot with NO entity resolution) is untouched: the gate
        // reads only the musician slot's ER, so today's search path runs.
        var pnk = Pnk();
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateFindSongHandler(IndexOf(pnk));
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(musicianSlot: null, titleKeywords: "pink"),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(),
            FindSongSession(FindSongState.AwaitingArtist, keywords: "wish you were here"),
            CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        TestHelpers.AssertSessionOpen(response, "the flow continues with an open elicit");
        Assert.NotNull(ReadFindSongState(response));
    }

    [Fact]
    public async Task FindSong_AwaitingArtist_Ask_SupersedesFlowState_AndYesRoutesToYesIntentHandler()
    {
        // THE coexistence pin (the JF-690 deferral's feared interplay, resolved
        // by the JF-398 machinery): the ask fired on an open FindSong flow must
        // strip FindSongSessionData from the session, because the interceptor
        // honors AskMultipleArtists' MarkOthersInactive removal marker. If the
        // FindSong key survived, HandlerSelector's force-route would swallow the
        // user's "yes" and feed it to FindSongIntentHandler as an artist answer,
        // dead-ending the disambiguation the ask just opened.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(IndexOf(pnk, floyd));
        IntentRequest askRequest = CreateFindSongIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"));
        var incomingSession = new global::Alexa.NET.Request.Session
        {
            New = false,
            Attributes = FindSongSession(FindSongState.AwaitingArtist, keywords: "wish you were here")
        };
        SkillResponse ask = await handler.HandleAsync(
            askRequest, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(),
            incomingSession.Attributes, CancellationToken.None);
        TestHelpers.AssertMultiArtistAsk(ask, pnk, floyd);

        // The interceptor merge over the open FindSong session: incoming keys are
        // merged, then the removal marker strips the FindSong flow key.
        var interceptorContext = new RequestContext(askRequest, _fx.CreateContext(), incomingSession, handler)
        {
            Response = ask
        };
        await new SessionAttributesInterceptor(Mock.Of<Microsoft.Extensions.Logging.ILogger<SessionAttributesInterceptor>>())
            .ProcessAsync(interceptorContext, CancellationToken.None);

        Assert.NotNull(ask.SessionAttributes);
        Assert.Null(ask.SessionAttributes!.GetValueOrDefault(FindSongIntentHandler.SessionDataKey));
        Assert.NotNull(ask.SessionAttributes.GetValueOrDefault(DisambiguationHelper.AttrMatches));

        // The next turn: the user's "yes" routes to YesIntentHandler through the
        // normal CanHandle order (no force-route), and the control proves the
        // force-route still owns a session that KEPT its FindSong state.
        using var harness = new DispatchHarness();
        var yesIntent = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.AmazonYes },
            Locale = "it-IT",
            RequestId = "test-req"
        };
        RoutingDecision afterAsk = harness.Select(
            DispatchHarness.CreateSkillRequest(yesIntent, sessionAttributes: ask.SessionAttributes));
        Assert.IsType<YesIntentHandler>(afterAsk.Handler);
        Assert.False(afterAsk.ForceRouted);

        Dictionary<string, object> stillFindSong = FindSongSession(FindSongState.AwaitingArtist);
        stillFindSong[DisambiguationHelper.AttrMatches] = ask.SessionAttributes[DisambiguationHelper.AttrMatches]!;
        RoutingDecision control = harness.Select(
            DispatchHarness.CreateSkillRequest(yesIntent, sessionAttributes: stillFindSong));
        Assert.IsType<FindSongIntentHandler>(control.Handler);
        Assert.True(control.ForceRouted);
    }

    // ===============================================================
    // JF-715: the gate-consume composite (probe ownership) and the
    // SearchAsync pool threading (one GetArtists per fall-through leg)
    // ===============================================================

    /// <summary>
    /// The zero-resolve fall-through shape shared by the pool-threading pins: a
    /// multi-value ER whose candidates name NO library artist exactly (the
    /// stale-catalog rank-#1 "Pink Floyd Live" plus "Tribute Band"), while the
    /// search's tier-2 prefix+fuzzy still lands on the contained "Pink Floyd"
    /// (the JF-702 PlayArtistSongs pin's proven query shape).
    /// </summary>
    private static Slot ZeroResolveMusician()
        => TestHelpers.ResolvedSlotMultiValue("pink", "Pink Floyd Live", "Tribute Band");

    /// <summary>
    /// Library mock for the artist-scoped legs: any ArtistIds query returns the
    /// given items (the artist-songs fetch and FindSong's scoped song search);
    /// everything else is empty (the ready in-memory index serves the artist
    /// search itself, so no MusicArtist query runs).
    /// </summary>
    private void SetupArtistScopedLibrary(params BaseItem[] items)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.ArtistIds != null && q.ArtistIds.Length > 0
                ? new List<BaseItem>(items)
                : new List<BaseItem>());
    }

    [Fact]
    public async Task PlaySong_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        // The composite's fall-through search is SEEDED with the gate's pool: the
        // counting index sees exactly ONE GetArtists (the gate's; SearchAsync's
        // tier-1 materialization is skipped), never the second fetch the
        // pre-JF-715 leg paid. The search still resolves the artist through the
        // threaded pool (the generic-word bypass then plays the artist's songs).
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        SetupArtistScopedLibrary(song);

        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            counting);
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlaySongIntent("la musica", ZeroResolveMusician()),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task AddToQueue_ZeroResolveFallThrough_SpeaksArtistNotFound_SearchConsumesTheGatePool()
    {
        // The composite's Terminal leg: the fall-through search finds nothing and
        // the composite speaks the artist not-found (the adopt-vs-not-found tail
        // the handlers used to own inline). One GetArtists total.
        var counting = new CountingArtistIndex(IndexOf(Abba()));
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>());

        var handler = CreateQueueHandler(counting);
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", ZeroResolveMusician(), IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession == true, "the not-found Tell ends the session");
        Assert.Contains("pink", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task PlayNext_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>(), song);

        var handler = CreatePlayNextHandler(counting);
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la musica", ZeroResolveMusician(), IntentNames.PlayNext),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task PlayAlbum_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        var (album, tracks) = MakeAlbumRelease("Wish You Were Here", 1975, 2, "Shine On You Crazy Diamond");
        _fx.SetupUserMock();
        _fx.SetupIndefiniteAlbumCatalog(
            floyd,
            new List<BaseItem> { album },
            tracks,
            new Dictionary<Guid, BaseItem> { [album.Id] = tracks[0] });

        var handler = CreateAlbumHandler(counting);
        SkillResponse response = await handler.HandleAsync(
            CreateAlbumIntent(album: null, ZeroResolveMusician()),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        var play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(tracks[0].Id.ToString(), play!.AudioItem.Stream.Token);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task QueryArtistLibrary_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        var track = new Audio { Name = "Money", Id = Guid.NewGuid() };
        SetupArtistScopedLibrary(track);

        var handler = CreateQueryHandler(counting);
        SkillResponse response = await handler.HandleAsync(
            CreateQueryIntent(ZeroResolveMusician()),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.Contains(track.Name, TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task FindSong_FirstTurn_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        _fx.SetupUserMock();

        var handler = CreateFindSongHandler(counting);
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(ZeroResolveMusician()),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "the keywords elicit keeps the session open");
        TestHelpers.AssertNoDisambiguationState(response);
        FindSongSessionData? state = ReadFindSongState(response);
        Assert.NotNull(state);
        Assert.Equal(FindSongState.AwaitingKeywords, state!.State);
        Assert.Equal(floyd.Id, state.ArtistId);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task FindSong_AwaitingArtist_ZeroResolveFallThrough_SearchConsumesTheGatePool()
    {
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        SetupArtistScopedLibrary(song);

        var handler = CreateFindSongHandler(counting);
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateFindSongIntent(ZeroResolveMusician()),
            _fx.CreateContext(), _fx.CreateUser(), session,
            FindSongSession(FindSongState.AwaitingArtist, keywords: "wish you were here"),
            CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(1, counting.GetArtistsCalls);
    }

    [Fact]
    public async Task AddToQueue_CarrierBleedGenericWord_CompositeNormalization_OpensTheGate()
    {
        // The probe-ownership pin (the addendum's fold): the raw slot "la canzone
        // musica" is a carrier-bleed generic word. Pre-JF-715 this site probed
        // the RAW value (article-strip "la" -> "canzone musica", not a member)
        // and the gate stayed closed; the composite's owned normalization strips
        // the carrier first ("musica") and the gate asks. This is the one shape
        // where the fold is observable, so it is pinned HERE, on a twin.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var queries = new List<InternalItemsQuery>();
        SetupQueueLibrary(queries, new List<BaseItem>());

        var handler = CreateQueueHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateQueueIntent("la canzone musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"), IntentNames.AddToQueue),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    // ===============================================================
    // PlayArtistSongs: the JF-702 pool-sharing closure
    // ===============================================================

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_Jf420Gate_ReusesTheGatePoolFetch_NoSecondMaterialization()
    {
        // The pool-sharing seam closed by JF-702: on a multi-value leg that
        // falls through to the search and then fires the JF-420
        // containment-vs-alternative gate, the scoped pool is materialized ONCE
        // inside the arbitration (its Pool ships on the result) and the JF-420
        // gate consumes that fetch. The counting index therefore sees exactly
        // TWO GetArtists calls (the gate's and SearchAsync's tier-1), never the
        // third the pre-JF-702 re-fetch paid.
        var floyd = PinkFloyd();
        var counting = new CountingArtistIndex(IndexOf(floyd));

        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.ArtistIds != null && q.ArtistIds.Length > 0
                ? new List<BaseItem> { song }
                : new List<BaseItem>());

        var handler = new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            counting);

        // The ER rank #1 is a MULTI-WORD canonical that no library artist
        // exact-matches (so the ambiguity does not collapse and the search
        // runs), while the search's tier-2 prefix+fuzzy still lands on the
        // contained "Pink Floyd", the exact shape the JF-420 gate exists for.
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            new IntentRequest
            {
                Intent = new Intent
                {
                    Name = IntentNames.PlayArtistSongs,
                    Slots = new Dictionary<string, Slot>
                    {
                        ["musician"] = TestHelpers.ResolvedSlotMultiValue("pink", "Pink Floyd Live", "Tribute Band")
                    }
                },
                Locale = "it-IT",
                RequestId = "test-req"
            },
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        // The request reached the play tail through the JF-420 gate's block
        // (single containment match, no alternative, no disambiguation).
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(2, counting.GetArtistsCalls);
    }

    public void Dispose()
    {
        _albumQueueManager.Dispose();
        _fx.LoggerFactory.Dispose();
    }
}
