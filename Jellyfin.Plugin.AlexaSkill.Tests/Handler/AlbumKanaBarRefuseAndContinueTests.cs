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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-776 section B: the album kana bars' interaction pair found by the JF-773
/// code review. B1: <see cref="FuzzyMatcher.FindBestMatchWithScore"/> early-exits
/// at the first containment-class (>= 90) winner, so when a kana-tagged library
/// holds BOTH the exact album and a suffixed sibling whose READING contains the
/// query, and the DB lists the suffixed one first (GetItemList with no OrderBy,
/// the JF-427 note), the walk returns the suffixed winner, the JF-661/JF-662 bar
/// refuses it on the length band, and the old refuse-and-stop answered the album
/// not-found although the exact album exists - reachability on JF-773's own
/// target shape was order-dependent. These pins hold the refuse-and-CONTINUE
/// walk (the JF-412 embedded-walk pattern applied to the bar refusal at BOTH
/// album arms): the suffixed sibling listed FIRST no longer shadows the exact
/// album, while a bait with no alternate above threshold still lands the honest
/// not-found (the JF-661/JF-662 bait class). B2: the strip helper was ASCII-only,
/// so the standard Japanese full-width parenthetical (U+FF08/U+FF09, which the
/// romanizer passes through unchanged) kept its suffix and failed the band; the
/// identically shaped Latin 'Yorunikakeru (Deluxe)' stripped, collided, and
/// played. 'ヨルニカケル' reads 'yorunikakeru', 'ヨルニカケルデラックス' reads
/// 'yorunikakeruderakkusu' (the JF-773 fixtures).
/// </summary>
[Collection("Plugin")]
public class AlbumKanaBarRefuseAndContinueTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private const string KanaAlbumName = "ヨルニカケル";
    private const string KanaSuffixedName = "ヨルニカケルデラックス";
    private const string RomajiReading = "yorunikakeru";

    private static IntentRequest AlbumIntent(string album, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot> { ["album"] = new Slot { Name = "album", Value = album } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private PlayAlbumIntentHandler CreateAlbumHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf776-album-bar-walk");
        _fx.SetupUserMock();
    }

    // The album mock shape lives on the fixture/TestHelpers (JF-776's
    // third-copy hoist); the JF-427 order-dependence pins ride the mock's list
    // order, so the suffixed sibling is always listed FIRST where a test pins
    // it.

    // ---------------------------------------------------------------
    // B1: the suffixed-sibling shadow (both album arms)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayAlbum_SuffixedKanaSiblingListedFirst_ExactAlbumPlays_JF776()
    {
        // The B1 red proof: the suffixed sibling is listed FIRST (the mock's
        // list order), the walk's containment-class early exit hands it to the
        // bar, the band refuses it (21 vs 12), and the refuse-and-stop shape
        // answered the album not-found with the exact album in the library.
        // The refuse-and-continue walk re-runs the ranking without the refused
        // winner and the exact album (100, band 0) plays.
        SetupPlugin();
        var (suffixed, _) = TestHelpers.MakeAlbum(KanaSuffixedName);
        var (exact, exactTracks) = TestHelpers.MakeAlbum(KanaAlbumName);
        _fx.SetupExactMissWithFuzzyAlbums(new List<BaseItem> { suffixed, exact });
        _fx.SetupAlbumTracks(exact, exactTracks);

        SkillResponse response = await CreateAlbumHandler().HandleAsync(
            AlbumIntent(KanaAlbumName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the exact album must not be shadowed by the suffixed sibling the bar refuses");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain(KanaSuffixedName, speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlbumCascade_SuffixedKanaSiblingListedFirst_ExactAlbumPlays_JF776()
    {
        // The cascade arm's mirror of the same walk (AlbumPlayService's JF-661
        // bar at the 90 containment-grade threshold).
        SetupPlugin();
        var (suffixed, _) = TestHelpers.MakeAlbum(KanaSuffixedName);
        var (exact, exactTracks) = TestHelpers.MakeAlbum(KanaAlbumName);
        _fx.SetupExactMissWithFuzzyAlbums(new List<BaseItem> { suffixed, exact });
        _fx.SetupAlbumTracks(exact, exactTracks);

        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SkillResponse? result = await probe.CallTryAlbumFallbackAsync(
            KanaAlbumName, TestHelpers.CreateJellyfinUser(), _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, "jf776 cascade walk probe", CancellationToken.None, kanaOrigin: null);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "the cascade's walk must reach the exact album behind the refused suffixed sibling");
    }

    [Fact]
    public async Task PlayAlbum_SuffixedBaitAlone_KanaQuery_StillHonestNotFound_JF776()
    {
        // The refusal preservation pin (the JF-662 bait class): with no
        // alternate above threshold, walking down the ranking exhausts the
        // candidates and the honest album not-found stands exactly as the
        // refuse-and-stop shape produced it.
        SetupPlugin();
        var (suffixed, _) = TestHelpers.MakeAlbum(KanaSuffixedName);
        _fx.SetupExactMissWithFuzzyAlbums(new List<BaseItem> { suffixed });

        SkillResponse response = await CreateAlbumHandler().HandleAsync(
            AlbumIntent(KanaAlbumName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the album not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundAlbumByName", "ja-JP", RomajiReading), speech, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // B2: the full-width parenthetical (U+FF08/U+FF09) strip
    // ---------------------------------------------------------------

    [Fact]
    public void AlbumKanaBar_FullWidthParentheticalReading_Collides_JF776()
    {
        // The strip-helper pin: 'ヨルニカケル（デラックス）' reads
        // 'yorunikakeru（derakkusu）' (the romanizer passes non-kana through), the
        // ASCII-only strip left the suffix, and the band refused (|23-12| = 11 >
        // 3). Teaching the helper the full-width pair strips to the reading
        // core (band 0, identical codes) and the album collides exactly like
        // the identically shaped Latin 'Yorunikakeru (Deluxe)'.
        var album = new MusicAlbum { Name = "ヨルニカケル（デラックス）", Id = Guid.NewGuid() };

        Assert.True(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, album));
    }

    [Fact]
    public async Task PlayAlbum_FullWidthParentheticalAlbum_KanaQuery_Plays_JF776()
    {
        // The B2 handler red proof: the fuzzy arm's containment match
        // ('yorunikakeru' inside the reading) is bar-refused pre-fix (the
        // suffix survives the ASCII-only strip and widens the band) and plays
        // post-fix; the announcement still speaks the DISPLAY name.
        SetupPlugin();
        var (album, tracks) = TestHelpers.MakeAlbum("ヨルニカケル（デラックス）");
        _fx.SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        _fx.SetupAlbumTracks(album, tracks);

        SkillResponse response = await CreateAlbumHandler().HandleAsync(
            AlbumIntent(KanaAlbumName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the full-width parenthetical form must play through the bar like the ASCII one");
    }

    [Fact]
    public void AlbumKanaBar_AsciiParentheticalMatrix_Unchanged_JF776()
    {
        // The ASCII control matrix: the Latin forms keep their exact pre-fix
        // verdicts (strip + band + codes), so only the full-width pair was
        // added to the ONE definition. A trailing parenthetical is metadata
        // and strips (stacked groups too), widening the band only for the
        // NON-parenthetical suffix shape ('Yorunikakeru Deluxe', the honest
        // widened refusal).
        var strips = new MusicAlbum { Name = "Yorunikakeru (Deluxe)", Id = Guid.NewGuid() };
        Assert.True(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, strips));

        var stacked = new MusicAlbum { Name = "Yorunikakeru (Deluxe) (Live)", Id = Guid.NewGuid() };
        Assert.True(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, stacked));

        var widened = new MusicAlbum { Name = "Yorunikakeru Deluxe", Id = Guid.NewGuid() };
        Assert.False(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, widened));
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
