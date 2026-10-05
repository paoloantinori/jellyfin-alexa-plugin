using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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
/// JF-659: the ER canonical for musician slots (the genre-canonical pattern of
/// JF-642 applied to the catalog-backed JellyfinArtist type). Naturalized ja
/// voice selects PlayArtistSongs with ER_SUCCESS_MATCH ('クイーン' resolving to
/// the canonical 'Queen'), but the handler used to resolve from the RAW slot,
/// romanize to 'kuin', and hit the Queen/Keane Double Metaphone tie, firing the
/// multi-artist ask on every tie-shaped name despite the resolved entity. With
/// the canonical feeding the search, the exact library name resolves directly.
/// The raw-slot path (no ER, the simulator shape) keeps today's honest ask
/// (the control legs here; KanaOriginAcceptanceTests owns the original pins).
/// </summary>
[Collection("Plugin")]
public class MusicianErCanonicalTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private BaseItem Queen() => new MusicArtist { Name = "Queen", Id = Guid.NewGuid() };
    private BaseItem Keane() => new MusicArtist { Name = "Keane", Id = Guid.NewGuid() };

    private static IntentRequest CreateArtistIntent(string musician, string? canonical = null)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        // The ER_SUCCESS_MATCH shape the JellyfinArtist catalog produces for a
        // spoken kana variant resolving to the canonical library name
        // (TestHelpers.ResolvedSlot, with null canonical for the raw-slot legs).
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot(musician, canonical) };
        return new IntentRequest { Intent = intent, Locale = "ja-JP", RequestId = "test-req" };
    }

    private static IntentRequest CreateSongIntent(string song, string musician, string canonical)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song },
            ["musician"] = TestHelpers.ResolvedSlot(musician, canonical)
        };
        return new IntentRequest { Intent = intent, Locale = "ja-JP", RequestId = "test-req" };
    }

    private PlayArtistSongsIntentHandler CreateArtistHandler(IArtistIndex index)
        => new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private PlaySongIntentHandler CreateSongHandler(IArtistIndex index)
        => new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private static bool HasAudioPlayerDirective(SkillResponse response)
        => TestHelpers.GetPlayDirective(response) != null;

    private static bool IsDisambiguationAsk(SkillResponse response, params string[] expectedNames)
    {
        if (response.Response.ShouldEndSession == true || HasAudioPlayerDirective(response))
        {
            return false;
        }

        var matches = response.SessionAttributes?.GetValueOrDefault(DisambiguationHelper.AttrMatches)?.ToString();
        return matches != null && expectedNames.All(n => matches.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------
    // PlayArtistSongs: the ER-resolved tie shape plays, no extra turn
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_ErResolvedMusician_PlaysCanonicalArtist_WithoutTieAsk_JF659()
    {
        // The live incident shape: 'クイーン' carries ER_SUCCESS_MATCH to 'Queen'
        // while the index holds the Queen/Keane DM-colliding pair. The canonical
        // feeds the search, so the exact library name resolves and the response
        // plays Queen's songs (AudioPlayer.Play carrying Queen's track) instead of
        // the multi-artist tie ask.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, FakeArtistIndex.CodesFromArtistNames(queen, keane));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン", canonical: "Queen"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the ER-resolved artist must play directly");
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.False(IsDisambiguationAsk(response, "Queen", "Keane"), "the tie ask must not fire once ER resolved the entity");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == queen.Id);
    }

    [Fact]
    public async Task HandleAsync_RawKanaMusician_NoResolution_KeepsTheTieAsk_JF659()
    {
        // Control leg (KanaOriginAcceptanceTests owns the original pin): the same
        // colliding pair WITHOUT entity resolution keeps the honest ask, because
        // the raw romanized query genuinely cannot distinguish the winner.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, FakeArtistIndex.CodesFromArtistNames(queen, keane));

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(IsDisambiguationAsk(response, "Queen", "Keane"), "the raw kana path keeps today's tie ask");
        Assert.False(HasAudioPlayerDirective(response));
    }

    // ---------------------------------------------------------------
    // JF-755 (the 2026-09-29 JF-658 review finding, moved from JF-645
    // item 4): the KANA canonical against a mixed library
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_KanaCanonical_MixedLibrary_PlaysKanaNamedArtist_JF755()
    {
        // The finding's shape: the catalog value itself is kana ('クイーン'), a
        // kana-tagged artist in an otherwise Latin library holding the Queen/Keane
        // DM-colliding pair. The JF-658 fold routed the canonical through
        // SearchAsync's entry romanization for the first time, so the query became
        // 'kuin', lost the kana artist, and opened the tier-4 91-tie to a Latin
        // artist while the JF-652 bar stayed inert (kanaOrigin=false whenever a
        // canonical resolved). The symmetric index's romaji key makes the
        // romanized canonical exact-hit the kana artist at tier 1, so the ER
        // evidence resolves to the artist it names and plays it.
        var kanaQueen = new MusicArtist { Name = "クイーン", Id = Guid.NewGuid() };
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(
            new[] { kanaQueen, queen, keane },
            FakeArtistIndex.CodesFromArtistNames(kanaQueen, queen, keane));
        var song = new Audio { Name = "ボヘミアン・ラプソディ", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン", canonical: "クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the kana canonical must resolve to the kana-named artist and play");
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.False(IsDisambiguationAsk(response, "Queen", "Keane"), "the Latin tie pair must not be offered for a kana canonical");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == kanaQueen.Id);
        Assert.DoesNotContain(artistScopedSongQueries, ids => ids.Contains(queen.Id) || ids.Contains(keane.Id));
    }

    [Fact]
    public async Task HandleAsync_KanaCanonical_KanaArtistAbsent_FuzzyRecoversLikeALatinCanonical_JF755()
    {
        // The stale-catalog residual, pinned at its ACCEPTED boundary (the design
        // decision in the task file): when the kana-named artist the canonical
        // points at is NOT in the library, the romanized query falls to the tier-4
        // 91-collision class with the JF-652 bar inert (kanaOrigin stays false, the
        // JF-659 invariant), and the recovery behaves exactly like a LATIN canonical
        // whose artist is absent: the colliding Latin artist fuzzy-recovers and
        // plays. That is by design, not a regression to block: the user asked for
        // the real-world artist (クイーン IS Queen), and the deleted entry is the
        // catalog's staleness, not a wrong play. The tier-4 pick is deterministic
        // in fixture order (the first 91-tie wins under the strict-greater rule).
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(
            new[] { queen, keane },
            FakeArtistIndex.CodesFromArtistNames(queen, keane));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン", canonical: "クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the resolved-but-absent entity fuzzy-recovers by design, as for a Latin canonical");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == queen.Id);
    }

    // ---------------------------------------------------------------
    // JF-755 code review: the judgment layers see the matched reading
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_RomajiExactMatch_WithLatinSuperstringRival_PlaysTheKanaArtist_JF755()
    {
        // The multi-match leg: 'ニル' (romaji key 'niru') and 'Niru & Taro' BOTH
        // match tier 1 for the raw kana query (Contains over the query-side names).
        // Pre-fix, HandleFuzzyMiss scored raw names, so the Latin rival's containment
        // 90 won the auto-accept while the exact kana match scored 0 and vanished;
        // the JF-652 bar then refused the rival (no code collision: 'Niru & Taro'
        // encodes NRT against the query's NR) and the honest answer was the
        // NOT-FOUND despite the library holding the exact artist. Post-fix the
        // scoring selector reads the romaji key: the exact match wins 100, clears
        // the kana bar on a real collision, and the rival (outside the collision
        // bar) is no near-tie, so the kana artist plays.
        var kanaNiru = new MusicArtist { Name = "ニル", Id = Guid.NewGuid() };
        var latinRival = new MusicArtist { Name = "Niru & Taro", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(
            new[] { kanaNiru, latinRival },
            FakeArtistIndex.CodesFromArtistNames(kanaNiru, latinRival));
        var song = new Audio { Name = "ニルの歌", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("ニル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the exact romaji match through the kana artist's key must play");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == kanaNiru.Id);
        Assert.DoesNotContain(artistScopedSongQueries, ids => ids.Contains(latinRival.Id));
    }

    [Fact]
    public async Task HandleAsync_KanaNamedArtist_RomajiContainmentQuery_GetsTheJf377Ask_JF755()
    {
        // The string-gate leg: a kana-named artist ('ペイン', romaji key 'pein')
        // reached through a qualifier query ('pein no uta') is the coincidental-
        // containment shape; against the raw kana name the JF-377 predicate was
        // script-blind and the match auto-played silently where the Latin
        // equivalent ('Pain' inside a qualifier query) gets the yes/no ask. The
        // matched reading pair makes the gate fire; the ask SPEAKS the kana name.
        var kanaPein = new MusicArtist { Name = "ペイン", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(
            new[] { kanaPein }, FakeArtistIndex.CodesFromArtistNames(kanaPein));

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("pein no uta"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(HasAudioPlayerDirective(response), "a coincidental containment must downgrade to the ask, not auto-play");
        Assert.True(IsDisambiguationAsk(response, "ペイン"), "the ask presents the kana-named artist (speech keeps the raw name)");
    }

    // ---------------------------------------------------------------
    // PlaySong: the canonical scopes the artist filter deterministically
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_ErResolvedMusician_ScopesSongSearchToCanonicalArtist_JF659()
    {
        // The musician filter resolves through the canonical, so the artist-scoped
        // song search carries Queen's id (not the romanized 'kuin' coin flip
        // between the DM-colliding pair) and the found song plays.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, FakeArtistIndex.CodesFromArtistNames(queen, keane));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0 && q.SearchTerm != null)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds == null || q.ArtistIds.Length == 0)))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("bohemian rhapsody", "クイーン", "Queen"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the ER-resolved artist filter must lead to a play");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == queen.Id);
    }

    // ---------------------------------------------------------------
    // Gate review round 2: the F1 evidence rule on the miss path
    // (an ER-resolved artist name is never guessed as a TITLE)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayAlbum_ErResolvedMusician_ArtistMiss_SkipsTitleRetry_HonestNotFound()
    {
        // The catalog-typed-locale shape (raw 'queen', canonical 'Queen', the
        // case-insensitive SearchTerm erases the difference): a stale-catalog
        // artist miss must NOT retry the resolved name as an album title even
        // though a same-titled album exists; the honest not-found answers.
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        var request = CreateAlbumIntent("queen", "Queen");
        _fx.SetupUserMock();

        var baitAlbum = new MusicAlbum { Name = "Queen", Id = Guid.NewGuid() };
        var albumQueries = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicAlbum))
                {
                    albumQueries.Add(q.SearchTerm ?? string.Empty);
                    return new List<BaseItem> { baitAlbum };
                }

                return new List<BaseItem>();
            });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest not-found Tell ends the session");
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Empty(albumQueries);
    }

    [Fact]
    public async Task QueryArtistLibrary_ErResolvedMusician_ArtistMiss_SkipsSongFallback_HonestNotFound()
    {
        // The same evidence rule on the song-title fallback: the song index is
        // primed with a song TITLED like the resolved artist (score 95, far over
        // the 65 bar), and the ER-resolved miss must still speak the honest
        // artist not-found instead of playing it.
        var baitSong = new Audio { Name = "Queen", Id = Guid.NewGuid() };
        var handler = new QueryArtistLibraryIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: null,
            songNgramIndex: new TestHelpers.FakeSongIndex((baitSong, 95.0)));
        var request = CreateQueryArtistIntent("queen", "Queen");
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest not-found Tell ends the session");
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(response.Response.OutputSpeech);
    }

    // ---------------------------------------------------------------
    // FindSong: the canonical leg resolves the artist, the raw legs do not
    // ---------------------------------------------------------------

    [Fact]
    public async Task FindSong_ErResolvedMusician_FirstInvocation_ResolvesCanonicalArtist()
    {
        // The raw slot value ('zzzqqq') matches nothing on its own; only the ER
        // canonical ('Queen') resolves the artist, so the elicited session data
        // carrying the artist id proves the canonical drove the search.
        var queen = Queen();
        var index = new FakeArtistIndex(new[] { queen }, FakeArtistIndex.CodesFromArtistNames(queen));
        var handler = new FindSongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);
        _fx.SetupUserMock();

        var intent = new Intent { Name = IntentNames.FindSongByArtistIntent };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot("zzzqqq", "Queen") };
        var request = new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        string? sessionJson = response.SessionAttributes?.GetValueOrDefault(FindSongIntentHandler.SessionDataKey)?.ToString();
        Assert.NotNull(sessionJson);
        Assert.Contains(queen.Id.ToString(), sessionJson);
    }

    [Fact]
    public async Task FindSong_ErResolvedMusician_AwaitingArtist_ResolvesCanonicalArtist()
    {
        // The AwaitingArtist wiring: the musician leg carries the canonical while
        // the raw value alone resolves nothing; the stored session data must end
        // with the canonical artist resolved.
        var queen = Queen();
        var index = new FakeArtistIndex(new[] { queen }, FakeArtistIndex.CodesFromArtistNames(queen));
        var handler = new FindSongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);
        _fx.SetupUserMock();

        var intent = new Intent { Name = IntentNames.FindSongIntent };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot("zzzqqq", "Queen") };
        var request = new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
        var sessionAttributes = new Dictionary<string, object>
        {
            [FindSongIntentHandler.SessionDataKey] = JsonConvert.SerializeObject(new FindSongSessionData { State = FindSongState.AwaitingArtist })
        };

        SkillResponse response = await handler.HandleAsync(
            request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), sessionAttributes, CancellationToken.None);

        string? sessionJson = response.SessionAttributes?.GetValueOrDefault(FindSongIntentHandler.SessionDataKey)?.ToString();
        Assert.NotNull(sessionJson);
        Assert.Contains(queen.Id.ToString(), sessionJson);
    }

    private static IntentRequest CreateAlbumIntent(string musician, string canonical)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot(musician, canonical) };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static IntentRequest CreateQueryArtistIntent(string musician, string canonical)
    {
        var intent = new Intent { Name = IntentNames.QueryArtistLibrary };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot(musician, canonical) };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
