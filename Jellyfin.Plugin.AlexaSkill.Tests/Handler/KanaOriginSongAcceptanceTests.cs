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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-654: the song-side kana bar. A katakana query romanizes into a romaji class
/// whose score distribution the Latin-calibrated song acceptance machinery was
/// never calibrated for (live, deployed a9c57451: musician=ビートルズ refused the
/// wrong ARTIST via the JF-652 bar, but TrySongFallback then phonetic-matched
/// 'bitoruzu' to the song 'Bitters &amp; Absolut' over the 65 coverage bar and
/// auto-played it). A kana-origin query auto-plays a song only on a real
/// (length-banded) Double Metaphone collision between the romanized query and
/// the song title, or a near-exact (&gt;= 95) plain coverage score; a
/// plain-fuzzy-only match at the coverage bar is the honest not-found. Latin
/// queries never enter the gates (the whole pre-existing suite is the Latin
/// regression matrix; the Latin controls here pin the same contract at the
/// touched sites).
/// </summary>
[Collection("Plugin")]
public class KanaOriginSongAcceptanceTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    // The live wrong-accept bait: a short English title the romaji syllable soup
    // clears the keyword-coverage bars against.
    private BaseItem BittersBait() => new Audio { Name = "Bitters & Absolut", Id = Guid.NewGuid() };

    // A title whose Double Metaphone codes really collide with the romanized
    // 'クイーン' -> 'kuin' (the JF-652 load-bearing pair, verified there against the
    // production encoder).
    private BaseItem QueenSong() => new Audio { Name = "Queen", Id = Guid.NewGuid() };

    private SharedGateProbeHandler CreateProbe()
    {
        _fx.SetupUserMock();
        return new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
    }

    private SkillResponse? CallSongFallback(SharedGateProbeHandler probe, string query, string locale, TestHelpers.FakeSongIndex index, bool kanaOrigin)
        => probe.CallTrySongFallback(
            query, _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), locale,
            index, _fx.LibraryManager.Object, CancellationToken.None,
            kanaOrigin: kanaOrigin);

    // ---------------------------------------------------------------
    // TrySongFallback (the site the live battery exposed)
    // ---------------------------------------------------------------

    [Fact]
    public void TrySongFallback_KanaQuery_PlainFuzzyBaitOverBar_ReturnsNull()
    {
        // THE live shape (the exact live score was not captured; 68 is the
        // half-title-coverage phonetic class the battery observed): 'bitoruzu'
        // clears the 65 bar against 'Bitters & Absolut' with no LENGTH-BANDED
        // code collision and no near-exact score. The honest not-found (null),
        // never the wrong play.
        SkillResponse? result = CallSongFallback(
            CreateProbe(), "ビートルズ", "ja-JP", new TestHelpers.FakeSongIndex((BittersBait(), 68.0)), kanaOrigin: true);

        Assert.Null(result);
    }

    [Fact]
    public void TrySongFallback_LatinQuery_SameBaitOverBar_Plays()
    {
        // Latin control at the same gate: the coverage bar alone still accepts
        // (the kana bar composes, it does not replace; the Latin matrix is
        // byte-identical).
        SkillResponse? result = CallSongFallback(
            CreateProbe(), "bitters", "en-US", new TestHelpers.FakeSongIndex((BittersBait(), 68.0)), kanaOrigin: false);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "the Latin coverage-bar match must still play");
    }

    [Fact]
    public void TrySongFallback_KanaQuery_RealCodeCollision_Plays()
    {
        // 'クイーン' romanizes to 'kuin', whose codes collide with the song title
        // 'Queen': the collision leg carries the play at a phonetic-class score
        // (68, below the 95 plain leg).
        SkillResponse? result = CallSongFallback(
            CreateProbe(), "クイーン", "ja-JP", new TestHelpers.FakeSongIndex((QueenSong(), 68.0)), kanaOrigin: true);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "a real Double Metaphone collision must play");
    }

    [Fact]
    public void TrySongFallback_KanaQuery_NearExactPlainScore_Plays()
    {
        // The >= 95 plain leg: a near-exact coverage score accepts without a code
        // collision (the bait title at 96).
        SkillResponse? result = CallSongFallback(
            CreateProbe(), "ビートルズ", "ja-JP", new TestHelpers.FakeSongIndex((BittersBait(), 96.0)), kanaOrigin: true);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "a near-exact plain score must play");
    }

    [Fact]
    public void TrySongFallback_BelowCoverageBar_ReturnsNullWhateverTheFlag()
    {
        // Composition control: the pre-existing CrossMediaSongThreshold gate still
        // runs first (a 60-score bait never plays, kana or Latin).
        Assert.Null(CallSongFallback(
            CreateProbe(), "ビートルズ", "ja-JP", new TestHelpers.FakeSongIndex((BittersBait(), 60.0)), kanaOrigin: true));
        Assert.Null(CallSongFallback(
            CreateProbe(), "bitters", "en-US", new TestHelpers.FakeSongIndex((BittersBait(), 60.0)), kanaOrigin: false));
    }

    // ---------------------------------------------------------------
    // The bar itself (predicate + list form)
    // ---------------------------------------------------------------

    [Fact]
    public void PassesKanaOriginSongAcceptance_PlainScoreLegBoundary()
    {
        // The plain leg admits >= 95 and nothing below it without a collision
        // ('bitoruzu' vs 'Sator': the JF-652-verified non-colliding pair).
        var sator = new Audio { Name = "Sator", Id = Guid.NewGuid() };

        Assert.True(SongIndexSearch.PassesKanaOriginSongAcceptance("bitoruzu", sator, 95.0));
        Assert.False(SongIndexSearch.PassesKanaOriginSongAcceptance("bitoruzu", sator, 94.0));
        Assert.False(SongIndexSearch.PassesKanaOriginSongAcceptance("bitoruzu", sator, 68.0));
    }

    [Fact]
    public void PassesKanaOriginSongAcceptance_CollisionLeg()
    {
        // The collision leg carries a phonetic-class score; the live bait pair
        // CODE-collides (DM caps at 4 chars, first-word-dominated: both PTRS,
        // verified against the production encoder) but is refused by the length
        // band (8 vs 19 chars): the code cap cannot manufacture evidence out of a
        // short romaji query and a long multi-word title.
        Assert.True(SongIndexSearch.PassesKanaOriginSongAcceptance("kuin", QueenSong(), 68.0));
        Assert.False(SongIndexSearch.PassesKanaOriginSongAcceptance("bitoruzu", BittersBait(), 68.0));
    }

    [Fact]
    public void PassesKanaOriginSongAcceptance_MultiWordLegitKanaQuery_CollidesWithinBand()
    {
        // The legitimate multi-word kana class: the full katakana title
        // romanizes to the full romaji shape, collides on codes, and sits inside
        // the length band (17 vs 17) -> plays at a phonetic-class score.
        var rhapsody = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };
        Assert.True(SongIndexSearch.PassesKanaOriginSongAcceptance("bohemian rapusodi", rhapsody, 72.0));

        // Half the title (the 'one keyword against a multi-word title' shape)
        // falls outside the band and is the honest not-found.
        Assert.False(SongIndexSearch.PassesKanaOriginSongAcceptance("rapusodi", rhapsody, 72.0));
    }

    [Fact]
    public void PassesKanaOriginSongAcceptance_RemasteredTitle_ParentheticalsStrippedForBandAndCodes()
    {
        // JF-654 review round 2, finding 4: trailing parenthetical groups are
        // metadata, not phonetic content ('(2011 Remaster)' contributes nothing
        // the first-word-dominated DM code can see), so the band and the codes
        // read the stripped title: the legit remastered-title kana query collides
        // in band and plays...
        var remaster = new Audio { Name = "Bohemian Rhapsody (2011 Remaster)", Id = Guid.NewGuid() };
        Assert.True(SongIndexSearch.PassesKanaOriginSongAcceptance("bohemian rapusodi", remaster, 72.0));

        // ...while the bait still refuses: its suffix is consonant-bearing TITLE
        // content, not a parenthetical, so the strip leaves it long enough to
        // fail the band (the pinned wrong-accept shape, repeated for contrast).
        Assert.False(SongIndexSearch.PassesKanaOriginSongAcceptance("bitoruzu", BittersBait(), 68.0));
    }

    [Fact]
    public void ApplyKanaOriginBar_LatinQuery_NoOp()
    {
        // kanaOrigin false returns the list unchanged (contents and order): the
        // Latin acceptance paths are byte-identical.
        var bait = BittersBait();
        var scored = new List<(BaseItem Item, double Score)> { (bait, 68.0), (QueenSong(), 40.0) };

        var result = SongIndexSearch.ApplyKanaOriginBar(scored, "bitters", kanaOrigin: false);

        Assert.Same(scored, result);
    }

    [Fact]
    public void ApplyKanaOriginBar_KanaQuery_KeepsOnlyBarPassers()
    {
        var bait = BittersBait();
        var queen = QueenSong();
        var scored = new List<(BaseItem Item, double Score)> { (bait, 68.0), (queen, 68.0) };

        var result = SongIndexSearch.ApplyKanaOriginBar(scored, "kuin", kanaOrigin: true);

        var kept = Assert.Single(result);
        Assert.Equal(queen.Id, kept.Item.Id);
    }

    // ---------------------------------------------------------------
    // FindSong (the titleKeywords chain, kana keywords)
    // ---------------------------------------------------------------

    private FindSongIntentHandler CreateFindSongHandler(ISongNgramIndex songIndex)
        => new FindSongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: null,
            songNgramIndex: songIndex);

    private static Dictionary<string, object> FindSongAttributes(FindSongSessionData data)
        => new() { ["FindSongSessionData"] = JsonConvert.SerializeObject(data) };

    private static IntentRequest FindSongKeywordsAnswer(string keywords, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.FindSongIntent };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["titleKeywords"] = new Slot { Name = "titleKeywords", Value = keywords }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private void SetupLibraryEmpty()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());
    }

    private void SetupFindSongSearch()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf654-findsong");
        _fx.UserManager.Setup(um => um.GetUserById(It.IsAny<Guid>())).Returns(TestHelpers.CreateJellyfinUser());
        SetupLibraryEmpty();
    }

    [Fact]
    public async Task FindSong_KanaKeywords_PlainFuzzyBait_NoMatchRePromptNeverPlays()
    {
        SetupFindSongSearch();
        var handler = CreateFindSongHandler(new TestHelpers.FakeSongIndex((BittersBait(), 68.0)));

        var attrs = FindSongAttributes(new FindSongSessionData { State = FindSongState.AwaitingKeywords, Keywords = "stale" });
        SkillResponse response = await handler.HandleAsync(
            FindSongKeywordsAnswer("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession != true, "the honest outcome is the FindSongNoMatch re-prompt (an open Ask)");
        Assert.Contains(
            Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("FindSongNoMatch", "ja-JP"),
            TestHelpers.GetSpeechText(response),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindSong_KanaKeywords_RealCollision_Plays()
    {
        SetupFindSongSearch();
        var handler = CreateFindSongHandler(new TestHelpers.FakeSongIndex((QueenSong(), 68.0)));

        var attrs = FindSongAttributes(new FindSongSessionData { State = FindSongState.AwaitingKeywords, Keywords = "stale" });
        SkillResponse response = await handler.HandleAsync(
            FindSongKeywordsAnswer("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a real code collision must still auto-play on the FindSong path");
    }

    [Fact]
    public async Task FindSong_LatinKeywords_SameBait_Plays()
    {
        // Latin control: the single-match auto-play is unchanged for Latin queries.
        SetupFindSongSearch();
        var handler = CreateFindSongHandler(new TestHelpers.FakeSongIndex((BittersBait(), 68.0)));

        var attrs = FindSongAttributes(new FindSongSessionData { State = FindSongState.AwaitingKeywords, Keywords = "stale" });
        SkillResponse response = await handler.HandleAsync(
            FindSongKeywordsAnswer("bitters", locale: "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the Latin single-match auto-play must be unchanged");
    }

    // ---------------------------------------------------------------
    // PlaySong title fallback (the JF-383/JF-384 chain)
    // ---------------------------------------------------------------

    private PlaySongIntentHandler CreateSongHandler(ISongNgramIndex? ngramIndex = null)
        => new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            songNgramIndex: ngramIndex);

    private static IntentRequest CreateSongIntent(string song, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    [Fact]
    public async Task PlaySong_KanaTitle_PlainFuzzyBait_HonestNotFoundNeverPlays()
    {
        _fx.SetupUserMock();
        SetupLibraryEmpty();
        var handler = CreateSongHandler(new TestHelpers.FakeSongIndex((BittersBait(), 68.0)));

        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the song not-found Tell");
        Assert.Contains(
            Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("NotFoundSongByName", "ja-JP", "bitoruzu"),
            TestHelpers.GetSpeechText(response),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_KanaTitle_RealCollision_Plays()
    {
        _fx.SetupUserMock();
        SetupLibraryEmpty();
        var handler = CreateSongHandler(new TestHelpers.FakeSongIndex((QueenSong(), 68.0)));

        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a real code collision must still play on the PlaySong title fallback");
    }

    [Fact]
    public async Task PlaySong_LatinTitle_SameBait_Plays()
    {
        // Latin control: the title fallback's n-gram acceptance is unchanged.
        _fx.SetupUserMock();
        SetupLibraryEmpty();
        var handler = CreateSongHandler(new TestHelpers.FakeSongIndex((BittersBait(), 68.0)));

        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("bitters", locale: "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the Latin title-fallback match must still play");
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
