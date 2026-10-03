using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-663: the kana-origin bar on the playlist fuzzy surface
/// (BuildPlaylistPlayResponseAsync, the last ungated surface in the kana family).
/// The flow romanized the playlist name (JF-643) and plain-accepted through the
/// SearchItemsFuzzyAsync fallback, the site-level FuzzyMatch pre-check, and the
/// HandleFuzzyMiss auto-play with no kana awareness, so a kana-origin query's
/// romaji could silently play an unrelated Latin-named playlist. These pins hold
/// the bar and its capture point: the kana-origin bait takes the honest playlist
/// not-found at the fuzzy fallback and at the server multi-match branch (emptied
/// AND mixed candidate sets, plus the downstream prompt surfaces that must carry
/// only bar-passers), the identical Latin query keeps the recall, a real
/// collision still accepts, the flag is read on the POST-STRIP name (kana in a
/// stripped ja carrier is not transliteration evidence; kana in the name
/// survives the strip), and the single-server-hit literal tier stays ungated
/// (the JF-661 notes' doctrine criterion: a server-narrowed hit is a direct
/// play, the class PlaySong and PlayAlbum keep ungated). Matcher scores and
/// Double Metaphone outcomes here were dumped from the production matcher, not
/// assumed: 'bitoruzu' (from ビートルズ) scores 90 with full keyword coverage
/// against both 'Bitoruzu Deluxe' and 'Bitoruzu Live' (the containment floor),
/// while the collision band refuses both (15 vs 8 and 13 vs 8 against the band
/// of 3) even though the stripped core 'Bitoruzu' shares the PTRS code;
/// 'satoru' (from サトル) scores 100 against 'Satoru' with the identical
/// STR-cored collision passing the band at 6 vs 6, and scores 66 with NO
/// keyword coverage against 'Bitoruzu Deluxe' (the mixed-set bait); 'Bitters'
/// collides with 'bitoruzu' at 42 with no coverage (the prompt-shape pin's
/// narrowed best, below the pre-check bar); slot
/// 'Queenという' strips to the kana-free 'Queen' while 'ビートルズという'
/// strips to the kana 'ビートルズ'.
/// </summary>
[Collection("Plugin")]
public class KanaOriginPlaylistSurfaceTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf663-playlist-surface");
        _fx.SetupUserMock();
    }

    private static IntentRequest PlaylistIntent(string playlist, string locale = "ja-JP", string intentName = IntentNames.PlayPlaylist)
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string, Slot> { ["playlist"] = new Slot { Name = "playlist", Value = playlist } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private PlayPlaylistIntentHandler CreatePlayHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    private ShufflePlayIntentHandler CreateShuffleHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    /// <summary>
    /// Mocks the library so the shared flow's server SearchTerm tier
    /// (<c>GetItemsResult</c>) returns <paramref name="serverHits"/> and the
    /// SearchItemsFuzzyAsync 500-row scan (<c>GetItemList</c>, no SearchTerm)
    /// returns <paramref name="fuzzyScanItems"/> (the PlayPlaylistFuzzyFallback
    /// tier the pins target).
    /// </summary>
    private void SetupPlaylistQueries(List<BaseItem>? serverHits, List<BaseItem>? fuzzyScanItems)
    {
        var hits = serverHits ?? new List<BaseItem>();
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = hits, TotalRecordCount = hits.Count });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(fuzzyScanItems ?? new List<BaseItem>());
    }

    private static BaseItem PlaylistFolder(string name)
        => new Folder { Name = name, Id = Guid.NewGuid(), Tags = Array.Empty<string>() };

    /// <summary>
    /// The shared BaseItem statics stub scope (TestHelpers.StubBaseItemStatics,
    /// the hoisted former per-file twin); needed only by the pins whose server
    /// tier returns playlist rows.
    /// </summary>
    private IDisposable StubBaseItemStatics()
        => TestHelpers.StubBaseItemStatics(_fx.LibraryManager);

    /// <summary>
    /// The Folder stand-ins resolve no tracks (GetManageableItems is DB-coupled in
    /// the unit host), so an ACCEPTED match surfaces the session-ending
    /// PlaylistEmpty Tell, reachable only past the acceptance point; a refused one
    /// surfaces the NotFoundPlaylist Tell (the PlayPlaylistIntentHandlerTests
    /// assertion convention).
    /// </summary>
    private static void AssertAccepted(SkillResponse response, string locale, string romanizedQuery)
    {
        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "an accepted match with unresolvable tracks ends in the PlaylistEmpty Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("PlaylistEmpty", locale), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ResponseStrings.Get("NotFoundPlaylist", locale, romanizedQuery), speech, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRefusedAsNotFound(SkillResponse response, string locale, string romanizedQuery, string baitName)
    {
        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the playlist not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundPlaylist", locale, romanizedQuery), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(baitName, speech, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // The fuzzy fallback tier (the SearchItemsFuzzyAsync adoption)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayPlaylist_KanaTitle_FuzzyFallbackBait_HonestPlaylistNotFound_NeverPlay()
    {
        // The task's verification shape: playlist=ビートルズ romanizes to
        // 'bitoruzu', whose containment match on 'Bitoruzu Deluxe' scores exactly
        // 90 with full keyword coverage (the pre-fix silent auto-play class). The
        // threaded bar demands the collision evidence the 'Deluxe' suffix widens
        // the name past (15 vs 8, band 3): the honest playlist not-found.
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertRefusedAsNotFound(response, "ja-JP", "bitoruzu", "Bitoruzu Deluxe");
    }

    [Fact]
    public async Task PlayPlaylist_LatinTitle_SameFuzzyFallback_StillAccepts()
    {
        // The Latin control and the pre-fix shape in one: the IDENTICAL Latin text
        // with no kana provenance self-computes the flag false at the caller, the
        // bar is inert, and the fuzzy fallback's recall is byte-identical to
        // before the threading (only the kana provenance changes the outcome).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("bitoruzu", "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertAccepted(response, "en-US", "bitoruzu");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTitle_RealCollisionPlaylist_StillAccepts()
    {
        // Composition control: 'サトル' romanizes to 'satoru', which scores 100
        // against 'Satoru' with a real length-banded code collision (band 0). The
        // bar composes with the fallback's threshold and coverage gate, it does
        // not replace them: a colliding playlist still accepts on a kana-origin
        // query.
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Satoru") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertAccepted(response, "ja-JP", "satoru");
    }

    // ---------------------------------------------------------------
    // The server multi-match branch (FuzzyMatch pre-check + HandleFuzzyMiss)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayPlaylist_KanaTitle_ServerMultiMatchAllBait_HonestPlaylistNotFound()
    {
        // The pre-filter pin: the server SearchTerm tier returns two containment
        // baits ('Bitoruzu Deluxe' and 'Bitoruzu Live', both 90, both outside the
        // collision band), the branch the site-level FuzzyMatch pre-check and the
        // HandleFuzzyMiss auto-play disambiguate over. With the flag set the
        // candidate set narrows to the collision-passing playlists, empties, and
        // the flow takes the honest playlist not-found instead of silently
        // picking a bait.
        SetupPlugin();
        using var statics = StubBaseItemStatics();
        SetupPlaylistQueries(
            serverHits: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe"), PlaylistFolder("Bitoruzu Live") },
            fuzzyScanItems: null);

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertRefusedAsNotFound(response, "ja-JP", "bitoruzu", "Bitoruzu Deluxe");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTitle_ServerMultiMatchMixedSet_NarrowsToCollidingPlaylist()
    {
        // The partial-narrowing pin: one collision-backed
        // candidate ('Satoru', 100, band 6v6) plus one bait ('Bitoruzu Deluxe'
        // for 'satoru': 66 with no keyword coverage, outside the band). The
        // narrowing must keep exactly the colliding playlist, whose site
        // pre-check acceptance ends in the PlaylistEmpty Tell; the observable
        // failure modes differ (an inverted predicate leaves only the bait,
        // whose 66-without-coverage yields the open-session HandleFuzzyMiss
        // prompt, and over-narrowing yields the not-found Tell), so the
        // acceptance assertion discriminates both. The one break it cannot
        // observe (a downstream list swap back to the unfiltered set) picks the
        // same best candidate here; Folder stand-ins cannot distinguish WHICH
        // playlist accepted (the JF-526 off-host limitation); the prompt-shape
        // pin below closes that residual.
        SetupPlugin();
        using var statics = StubBaseItemStatics();
        SetupPlaylistQueries(
            serverHits: new List<BaseItem> { PlaylistFolder("Satoru"), PlaylistFolder("Bitoruzu Deluxe") },
            fuzzyScanItems: null);

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertAccepted(response, "ja-JP", "satoru");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTitle_MultiMatchPrompt_NamesOnlyCollisionBackedCandidates()
    {
        // The downstream narrowed-list pin: neither the HandleFuzzyMiss candidate
        // list nor the AskFirstMatch matches may swap back to the unfiltered
        // server set. 'Bitters' collides with 'bitoruzu' (PTRS, band 7 vs 8) but
        // scores 42 without keyword coverage, so the narrowed flow falls through
        // the site pre-check into the HandleFuzzyMiss Confirm prompt naming
        // 'Bitters' alone; with either list unfiltered the 90-scoring
        // coverage-passing bait 'Bitoruzu Deluxe' wins the pre-check and
        // silently plays instead, so the prompt-shape assertions catch the swap.
        SetupPlugin();
        using var statics = StubBaseItemStatics();
        SetupPlaylistQueries(
            serverHits: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe"), PlaylistFolder("Bitters") },
            fuzzyScanItems: null);

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession, "the narrowed best lands in the open-session HandleFuzzyMiss Confirm prompt");
        Assert.True(response.SessionAttributes?.ContainsKey("disambig_matches") == true, "the response is the disambiguation ask");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Bitters", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bitoruzu Deluxe", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayPlaylist_CarrierKanaLatinName_PostStripFlagFalse_StillAccepts()
    {
        // The capture-point pin: the flag is read on the
        // POST-STRIP name, so kana living only in the stripped ja carrier
        // (slot 'Queenという' strips to the verbatim-Latin 'Queen') is NOT
        // transliteration evidence and the bar stays inert: the multi-match
        // pre-check accepts 'Queen Greatest Hits' (90, full coverage) exactly
        // as before JF-663. Capturing on the raw slot would have barred this
        // query from its own verbatim matches.
        SetupPlugin();
        using var statics = StubBaseItemStatics();
        SetupPlaylistQueries(
            serverHits: new List<BaseItem> { PlaylistFolder("Queen Greatest Hits"), PlaylistFolder("Queen Ballads") },
            fuzzyScanItems: null);

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("Queenという"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertAccepted(response, "ja-JP", "Queen");
    }

    [Fact]
    public async Task PlayPlaylist_CarrierKanaKanaName_FlagSurvivesTheStrip_HonestPlaylistNotFound()
    {
        // The complement: when the kana is IN the name (slot 'ビートルズという'
        // strips to the kana 'ビートルズ'), the post-strip capture still sees it
        // and the bar stays live through the carrier strip (the fuzzy-fallback
        // bait 'Bitoruzu Deluxe' is the honest not-found, never a play).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("ビートルズという"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertRefusedAsNotFound(response, "ja-JP", "bitoruzu", "Bitoruzu Deluxe");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTitle_ServerSingleHit_StaysLiteralIndexedPlay()
    {
        // The doctrine boundary: the server SearchTerm tier itself narrows to ONE
        // playlist, the literal indexed direct play the JF-661 notes' criterion
        // keeps ungated (the class PlaySong's and PlayAlbum's primary tiers keep
        // ungated; a server-narrowed hit is not a plugin fuzzy pick). The bar must
        // stay on the fuzzy surfaces only, so the single containment hit still
        // accepts for a kana-origin query.
        SetupPlugin();
        using var statics = StubBaseItemStatics();
        SetupPlaylistQueries(
            serverHits: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") },
            fuzzyScanItems: null);

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertAccepted(response, "ja-JP", "bitoruzu");
    }

    // ---------------------------------------------------------------
    // ShufflePlay (the second production caller of the shared builder)
    // ---------------------------------------------------------------

    [Fact]
    public async Task ShufflePlay_KanaTitle_FuzzyFallbackBait_HonestPlaylistNotFound_NeverPlay()
    {
        // The threading pin for the second caller: ShufflePlayIntentHandler pins
        // the same raw-slot flag into the shared builder, so the shuffle arm of
        // the flow takes the same honest refusal (the JF-602 sibling-pin
        // precedent for the two playlist callers).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") });

        SkillResponse response = await CreateShuffleHandler().HandleAsync(
            PlaylistIntent("ビートルズ", intentName: IntentNames.ShufflePlay), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        AssertRefusedAsNotFound(response, "ja-JP", "bitoruzu", "Bitoruzu Deluxe");
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
