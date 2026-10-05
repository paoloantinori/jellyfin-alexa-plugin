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
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-777: the song-side and playlist-head single-point kana bars converted to the
/// JF-776 B1 refuse-and-continue walk. The shadow shape at every site: a
/// kana-tagged library holding BOTH the exact item and a SPACE-SEPARATED suffixed
/// sibling whose reading contains the query, with the sibling listed FIRST (the
/// DB/scorer listing order, the JF-427 note); the pick machinery's containment-class
/// early exit hands the sibling to the bar, the length band refuses it, and the
/// pre-fix refuse-and-stop answered the honest not-found with the exact item in
/// the library. The single-token fused sibling does NOT shadow (it fails the
/// full-keyword-coverage gate before the bar) - the pins use the space-separated
/// form, the reachable one. 'ヨルニカケル' reads 'yorunikakeru' (12),
/// 'ヨルニカケル デラックス' reads 'yorunikakeru derakkusu' (25, band 13 &gt; 3);
/// 'サトル' reads 'satoru' (6), 'サトル デラックス' reads 'satoru derakkusu'
/// (17, band 11 &gt; 3) - the JF-773/JF-776 fixtures.
/// JF-781 added the fourth site (SearchMedia's own fuzzy-pass gate,
/// PassesKanaSongGate): the JF-777 pins all used song fixtures, so the gate's
/// refuse-and-stop was invisible to them (Audio recovers through the song-title
/// retry); the non-Audio playable kinds had NO recovery leg, and the pins below
/// cover them.
/// </summary>
[Collection("Plugin")]
public class SongKanaBarRefuseAndContinueTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private const string KanaSongName = "ヨルニカケル";
    private const string KanaSuffixedSongName = "ヨルニカケル デラックス";
    private const string RomajiSongReading = "yorunikakeru";

    private static Audio Song(string name) => TestHelpers.CreateSong(name);

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf777-song-bar-walk");
        _fx.SetupUserMock();
    }

    // ---------------------------------------------------------------
    // Site 1: TrySongFallback's scored-chain head (CrossMediaFallback)
    // ---------------------------------------------------------------

    [Fact]
    public void TrySongFallback_SuffixedKanaSiblingListedFirst_ExactSongPlays_JF777()
    {
        // The red proof: the FakeSongIndex fixes the chain order (best-first as
        // the index contract states), the suffixed sibling heads it at a
        // coverage-class score the threshold admits, the band refuses it
        // (25 vs 12), and the refuse-and-stop shape nulled out with the exact
        // song (105, the JF-755 near-exact class) one entry behind. The walk
        // advances the head and the exact song plays.
        SetupPlugin();
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SkillResponse? result = probe.CallTrySongFallback(
            KanaSongName, _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            new TestHelpers.FakeSongIndex((Song(KanaSuffixedSongName), 90.0), (Song(KanaSongName), 105.0)),
            _fx.LibraryManager.Object, CancellationToken.None,
            kanaOrigin: true);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "the exact song must not be shadowed by the suffixed sibling the bar refuses");
    }

    [Fact]
    public void TrySongFallback_SuffixedBaitAlone_KanaQuery_StillReturnsNull_JF777()
    {
        // The refusal-preservation pin (the JF-654 bait class): with no
        // bar-passing alternate behind it, the walk exhausts the chain and the
        // honest null (the caller's own not-found) stands exactly as the
        // refuse-and-stop shape produced it.
        SetupPlugin();
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SkillResponse? result = probe.CallTrySongFallback(
            KanaSongName, _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            new TestHelpers.FakeSongIndex((Song(KanaSuffixedSongName), 90.0)),
            _fx.LibraryManager.Object, CancellationToken.None,
            kanaOrigin: true);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Site 2: SearchMedia's full-coverage FuzzyMatch pre-check
    // ---------------------------------------------------------------

    private SearchMediaIntentHandler CreateSearchMediaHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private static IntentRequest SearchMediaIntent(string query, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.SearchMedia };
        intent.Slots = new Dictionary<string, Slot> { ["query"] = new Slot { Name = "query", Value = query } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// Wires the playable-kind SearchTerm scan (the query carrying the Audio kind)
    /// to return <paramref name="songs"/> in the given order, and every other
    /// GetItemList shape (the artist-lookup tiers, the artist-items query is
    /// never reached) to miss, so the pre-check pool is exactly the mock's list.
    /// </summary>
    private void SetupSearchResults(List<BaseItem> songs)
    {
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.IncludeItemTypes != null
                && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio)
                && q.ArtistIds is not { Length: > 0 }
                ? songs
                : new List<BaseItem>());
    }

    [Fact]
    public async Task SearchMedia_SuffixedKanaSiblingListedFirst_ExactSongPlays_JF777()
    {
        // The red proof at the pre-check: the server lists the suffixed sibling
        // first, the FuzzyMatch leg's containment-class early exit (reading
        // 'yorunikakeru derakkusu' contains the query) hands it to the coverage
        // gate (which the space-separated form passes) and then the bar (which
        // the band refuses), and the refuse-and-stop answered MediaNotFound with
        // the exact song in the results. The walk removes the refused pick,
        // re-runs the FuzzyMatch on the remainder, and the exact song's 105
        // near-exact class auto-plays.
        SetupPlugin();
        SetupSearchResults(new List<BaseItem> { Song(KanaSuffixedSongName), Song(KanaSongName) });

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent(KanaSongName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the exact song must not be shadowed by the suffixed sibling the bar refuses");
    }

    [Fact]
    public async Task SearchMedia_SuffixedBaitWithUnrelatedSurvivor_KanaQuery_TheSurvivorsAskNeverOffersTheBait_JF777()
    {
        // The fall-through proof for the walk's non-empty outcome: the bait is
        // refused and removed, the unrelated survivor cannot clear the FuzzyMatch
        // threshold, and the flow lands in the survivors' ask naming the survivor
        // (the pre-fix refuse-and-stop answered MediaNotFound here; a regression
        // back to that shape fails the positive assert). The bait is never
        // auto-played and never offered: the removal, not the raw list, feeds the
        // fall-through, so HandleFuzzyMiss's >= 90 auto-accept cannot reach the
        // item the bar refused (the pre-fix refusal doctrine).
        SetupPlugin();
        SetupSearchResults(new List<BaseItem> { Song(KanaSuffixedSongName), Song("サトル") });

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent(KanaSongName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession != true, "the survivors' ask keeps the session open");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("サトル", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KanaSuffixedSongName, speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchMedia_AutoPlayUser_KanaQuery_BarRefusedSurvivor_NeverPlays_JF777()
    {
        // The CR1 hole-closure pin: HandleFuzzyMiss's AutoPlay disjunct bypasses
        // the coverage gate, so the partial-coverage survivor it auto-plays for
        // an AutoPlay user never met the pre-check's bar (the walk removed the
        // full-coverage bait, then broke on this survivor's partial coverage).
        // The delegate re-judges the bar: the survivor ('ヨルニカケル', whose
        // reading lacks the query's 'derakkusu' token, band 13) is refused and
        // the honest not-found stands - the same protection the sibling walk
        // inside SearchItemsFuzzyAsync gives its AutoPlay exemption.
        SetupPlugin();
        SetupSearchResults(new List<BaseItem> { Song("ヨルニカケル デラックス エディション"), Song(KanaSongName) });
        var autoPlayUser = _fx.CreateUser();
        autoPlayUser.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent($"{KanaSongName} デラックス"), _fx.CreateContext(), autoPlayUser, _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the bar-refused survivor must not ride the AutoPlay disjunct into a play");
    }

    // ---------------------------------------------------------------
    // Site 3: the playlist fuzzy-fallback head-check (AlbumPlayService,
    // riding SearchItemsFuzzyAsync's acceptanceBar)
    // ---------------------------------------------------------------

    private PlayPlaylistIntentHandler CreatePlayHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    private static BaseItem PlaylistFolder(string name)
        => new Folder { Name = name, Id = Guid.NewGuid(), Tags = Array.Empty<string>() };

    private void SetupPlaylistQueries(List<BaseItem>? serverHits, List<BaseItem>? fuzzyScanItems)
    {
        var hits = serverHits ?? new List<BaseItem>();
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = hits, TotalRecordCount = hits.Count });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(fuzzyScanItems ?? new List<BaseItem>());
    }

    

[Fact]
    public async Task PlayPlaylist_FuzzyFallback_SuffixedKanaSiblingListedFirst_ExactPlaylistAccepted_JF777()
    {
        // The red proof at the third single-point bar: the server SearchTerm
        // misses (the kana-tagged library shape), the fuzzy scan lists the
        // suffixed sibling first, the containment early exit hands it to the
        // bar, the band refuses it (17 vs 6), and the refuse-and-stop spoke
        // NotFoundPlaylist with the exact playlist in the scan. The walk inside
        // SearchItemsFuzzyAsync removes the refused winner and the exact
        // playlist is served (the Folder stand-in resolves no tracks, so the
        // accepted flow ends in the PlaylistEmpty Tell - every acceptance point
        // passed).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("サトル デラックス"), PlaylistFolder("サトル") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertPlaylistAccepted(response, "ja-JP", "satoru");
    }

    // ---------------------------------------------------------------
    // Site 4 (JF-781): SearchMedia's OWN fuzzy-pass gate
    // (PassesKanaSongGate, riding SearchItemsFuzzyAsync's acceptanceBar)
    // ---------------------------------------------------------------

    /// <summary>
    /// Wires every GetItemList shape to miss EXCEPT the fuzzy pass's scan
    /// (SearchTerm null, no name-tier fallback, no artist scoping, Limit 500),
    /// which returns <paramref name="scanItems"/> in the given order: the JF-427
    /// order-dependence rides the mock's list order (the suffixed sibling must be
    /// listed first for the containment early exit to hand it to the bar).
    /// <paramref name="onlyKind"/> narrows the serving scan to one single-kind
    /// IncludeItemTypes array, for the restricted-user pins where only the
    /// out-of-library sibling call (an all-Playlist kind set) may hit.
    /// </summary>
    private void SetupFuzzyScanOnly(List<BaseItem> scanItems, BaseItemKind? onlyKind = null)
    {
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.SearchTerm == null && q.NameStartsWith == null
                && q.NameContains == null && q.ArtistIds is not { Length: > 0 } && q.Limit == 500
                && (onlyKind == null || (q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == onlyKind))
                ? scanItems
                : new List<BaseItem>());
    }

    [Fact]
    public async Task SearchMedia_FuzzyPass_SuffixedKanaMovieSiblingListedFirst_ExactMoviePlays_JF781()
    {
        // The red proof at the gate, on a kind the retry cannot recover: the
        // primary SearchTerm scan and the artist fallback miss (the kana-tagged
        // library shape), the fuzzy scan lists the suffixed sibling first, the
        // containment early exit hands it to the coverage gate (passed,
        // space-separated) and then the bar (refused, 25 vs 12), and the pre-fix
        // refuse-and-stop answered MediaNotFound with the exact MOVIE in the scan:
        // the JF-506 song-title retry is Audio-only, so a video kind had no
        // recovery leg at all. The walk inside SearchItemsFuzzyAsync removes the
        // refused winner, re-picks on the remainder, and the exact movie's
        // near-exact class launches.
        SetupPlugin();
        SetupFuzzyScanOnly(new List<BaseItem> { TestHelpers.CreateMovie(KanaSuffixedSongName), TestHelpers.CreateMovie(KanaSongName) });

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent(KanaSongName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(
            response.Response.Directives?.Any(d => d is VideoAppLaunchDirective) == true,
            "the exact movie must not be shadowed by the suffixed sibling the bar refuses");
    }

    [Fact]
    public async Task SearchMedia_FuzzyPass_SuffixedKanaAlbumSiblingListedFirst_ExactAlbumPlays_JF781()
    {
        // The MusicAlbum leg of the same hole (the filing's non-Audio kind list):
        // identical mechanics, the audio-player launch arm of PlayItem.
        SetupPlugin();
        SetupFuzzyScanOnly(new List<BaseItem>
        {
            new MusicAlbum { Name = KanaSuffixedSongName, Id = Guid.NewGuid() },
            new MusicAlbum { Name = KanaSongName, Id = Guid.NewGuid() }
        });

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent(KanaSongName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the exact album must not be shadowed by the suffixed sibling the bar refuses");
    }

    [Fact]
    public async Task SearchMedia_FuzzyPass_SuffixedBaitAlone_KanaQuery_StillTheHonestNotFound_JF781()
    {
        // The refusal-preservation control (green pre-fix AND post-fix, the
        // JF-777 control shape): with no bar-passing alternate behind it, the
        // walked-out scan returns null, the Audio-only song-title retry misses
        // (no song index, a movie library), and the honest MediaNotFound stands
        // exactly as the refuse-and-stop shape produced it; the bait never
        // plays.
        SetupPlugin();
        SetupFuzzyScanOnly(new List<BaseItem> { TestHelpers.CreateMovie(KanaSuffixedSongName) });

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent(KanaSongName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.Directives?.Any(d => d is VideoAppLaunchDirective) != true,
            "the refused bait must never launch");
        Assert.True(response.Response.ShouldEndSession == true, "the walked-out scan is the honest MediaNotFound Tell");
    }

    [Fact]
    public async Task SearchMedia_RestrictedUser_OutOfLibraryFuzzyPass_SuffixedKanaPlaylistSiblingListedFirst_ExactPlaylistPlays_JF781()
    {
        // The SIBLING fuzzy call's bar (SearchMediaFuzzyOutOfLibrary): a
        // library-restricted user's playlist rides the out-of-library scope
        // (JF-456), so the walk conversion must arm the bar on BOTH calls. The
        // pre-fix refuse-and-stop left the exact playlist unrecovered with the
        // same Audio-only retry gap; post-fix the walk serves it.
        SetupPlugin();
        var playlist = new global::MediaBrowser.Controller.Playlists.Playlist { Name = "サトル", Id = Guid.NewGuid() };
        var suffixed = new global::MediaBrowser.Controller.Playlists.Playlist { Name = "サトル デラックス", Id = Guid.NewGuid() };
        SetupFuzzyScanOnly(new List<BaseItem> { suffixed, playlist }, onlyKind: BaseItemKind.Playlist);

        var restrictedUser = _fx.CreateUser();
        restrictedUser.AllowedLibraryIds = new List<string> { Guid.NewGuid().ToString() };

        SkillResponse response = await CreateSearchMediaHandler().HandleAsync(
            SearchMediaIntent("サトル"), _fx.CreateContext(), restrictedUser, _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the exact playlist must not be shadowed by the suffixed sibling the sibling-scope bar refuses");
    }

    private static IntentRequest PlaylistIntent(string playlist, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayPlaylist };
        intent.Slots = new Dictionary<string, Slot> { ["playlist"] = new Slot { Name = "playlist", Value = playlist } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
