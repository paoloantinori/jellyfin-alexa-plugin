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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-660: the kana-origin flag threaded into TryEntityFallbackAsync. The gate
/// self-computes its JF-652 artist-bar flag from the slot text pre-romanization,
/// which is exact only for raw-text callers; PlaySong, FindSong, and PlayAlbum
/// romanize their slots at entry (JF-643) and hand the gate the Latin local, so
/// the self-computed flag was false and the bar inert on those paths (live,
/// deployed a7d42b09: PlaySong song=ビートルズ refused the wrong SONG via the
/// JF-654 bar, then the JF-363 band offered the plain-fuzzy artist 'Sator'
/// because the artist-side flag never saw the kana). PlayAlbum is the THIRD
/// leaking caller: the task's caller map asserted it passes raw text, but it
/// romanizes the album slot in place at entry. These pins hold the threading:
/// the pinned flag restores the honest not-found on every pre-romanized caller,
/// and the identical Latin query with the flag unpinned still gets the JF-363
/// Confirm offer (the Latin matrix unchanged; matcher scores and Double
/// Metaphone codes here were dumped from the production matcher, not assumed).
/// </summary>
[Collection("Plugin")]
public class KanaOriginEntityFallbackThreadingTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    // The live bait: 'bitoruzu' scores 60 against 'Sator' (the JF-363 band
    // floor, no PTRS/STR collision); 'sato' scores 90 (the strict containment
    // floor, no ST/STR collision); 'satoru' codes collide with 'Sator' (STR)
    // and scores 100 through the phonetic boost.
    private BaseItem Sator() => new MusicArtist { Name = "Sator", Id = Guid.NewGuid() };

    private IArtistIndex SatorIndex(BaseItem sator) => new FakeArtistIndex(new[] { sator }, FakeArtistIndex.CodesFromArtistNames(sator));

    private void SetupEmptyLibrary()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf660-entity-fallback");
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());
    }

    private static IntentRequest SongIntent(string song, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot> { ["song"] = new Slot { Name = "song", Value = song } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static IntentRequest AlbumIntent(string album, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot> { ["album"] = new Slot { Name = "album", Value = album } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static IntentRequest KeywordsAnswer(string keywords, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.FindSongIntent };
        intent.Slots = new Dictionary<string, Slot> { ["titleKeywords"] = new Slot { Name = "titleKeywords", Value = keywords } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static Dictionary<string, object> FindSongAttributes(FindSongSessionData data)
        => new() { ["FindSongSessionData"] = JsonConvert.SerializeObject(data) };

    // ---------------------------------------------------------------
    // The gate: pinned flag vs self-computed on the identical input
    // ---------------------------------------------------------------

    private Task<SkillResponse?> CallEntityFallback(BaseItem sator, bool? kanaOrigin)
    {
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        _fx.SetupUserMock();
        return probe.CallTryEntityFallbackAsync(
            "bitoruzu", TestHelpers.CreateJellyfinUser(), _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, "kana probe", SatorIndex(sator), CancellationToken.None,
            notFoundMediaType: DisambiguationHelper.MediaTypeSong,
            kanaOrigin);
    }

    [Fact]
    public async Task TryEntityFallback_RomanizedQuery_PinnedKanaFlag_PlainFuzzySator_ReturnsNull()
    {
        // The exact input the pre-romanized callers hand the gate: the Latin
        // local 'bitoruzu' plus the flag pinned on the raw slot. The in-band
        // plain-fuzzy Sator match (60, no code collision) is the honest miss
        // under the JF-652 bar, never the JF-363 offer.
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        SkillResponse? result = await CallEntityFallback(Sator(), kanaOrigin: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryEntityFallback_RomanizedQuery_FlagUnpinned_SameSator_OffersArtist()
    {
        // The pre-fix shape and the Latin control in one: the IDENTICAL Latin
        // input with the flag left to self-compute (false: the text has no
        // kana), which is both what the bug produced and what a Latin-origin
        // query legitimately computes. The JF-363 Confirm band still offers
        // Sator: the band, the offer shape, and the Latin matrix are unchanged
        // by the threading; only the kana provenance changes the outcome.
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        SkillResponse? result = await CallEntityFallback(Sator(), kanaOrigin: null);

        Assert.NotNull(result);
        Assert.True(result!.Response.ShouldEndSession != true, "the JF-363 offer is an open Ask");
        Assert.True(result.Response.Directives == null || result.Response.Directives.Count == 0, "the offer plays nothing");
        Assert.Contains("Sator", TestHelpers.GetSpeechText(result), StringComparison.Ordinal);
        Assert.Equal("artist", result.SessionAttributes?["disambig_type"]);
        Assert.Equal("bitoruzu", result.SessionAttributes?["crossmedia_notfound_query"]);
        Assert.Equal("song", result.SessionAttributes?["crossmedia_notfound_type"]);
    }

    // ---------------------------------------------------------------
    // PlaySong (the live leak's caller)
    // ---------------------------------------------------------------

    private PlaySongIntentHandler CreateSongHandler(IArtistIndex artistIndex)
        => new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: artistIndex,
            songNgramIndex: new TestHelpers.FakeSongIndex());

    [Fact]
    public async Task PlaySong_KanaTitle_InBandPlainFuzzySator_HonestSongNotFound_NeverSatorOffer()
    {
        // The offer must be reachable for this null-assertion to prove the
        // bar refused it: pin Confirm so the global default cannot mask a leak.
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        // THE live leak shape (deployed a7d42b09): PlaySong song=ビートルズ, the
        // song search misses, and the cross-media artist fallback sees the
        // romanized 'bitoruzu' against the library's real Sator. The threaded
        // flag keeps the JF-652 bar live: the in-band plain-fuzzy match is the
        // honest song not-found, never the "did you mean Sator?" offer the
        // device spoke.
        SetupEmptyLibrary();
        var handler = CreateSongHandler(SatorIndex(Sator()));

        SkillResponse response = await handler.HandleAsync(
            SongIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the song not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundSongByName", "ja-JP", "bitoruzu"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_KanaTitle_StrictPlainFuzzySator_HonestSongNotFound_NeverAutoPlays()
    {
        // The strict arm of the same leak: 'サト' romanizes to 'sato', a tier-1
        // containment match on 'Sator' scoring 90 (over the strict bar) with no
        // Double Metaphone collision (ST vs STR). The inert flag auto-played
        // the wrong artist on this class; the threaded bar takes the honest
        // not-found (the documented ASR-truncation shape, 'crash' for 'Crash
        // Test Dummies', is the intended collision class this is NOT).
        SetupEmptyLibrary();
        var handler = CreateSongHandler(SatorIndex(Sator()));

        SkillResponse response = await handler.HandleAsync(
            SongIntent("サト"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the song not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundSongByName", "ja-JP", "sato"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_KanaTitle_RealCollision_StillPlaysTheArtist()
    {
        // Composition control: 'サトル' romanizes to 'satoru', whose Double
        // Metaphone codes collide with 'Sator' (STR/STR, phonetic-boosted 100).
        // The threaded bar composes with the strict gate, it does not replace
        // it: a real collision still auto-plays on the threaded caller.
        SetupEmptyLibrary();
        var sator = Sator();
        var song = new Audio { Name = "Wurlitzer", Id = Guid.NewGuid() };
        // Last Moq setup wins: the artist-songs query serves one track, every
        // other query stays empty.
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0
                ? new List<BaseItem> { song }
                : new List<BaseItem>().AsReadOnly());
        var handler = CreateSongHandler(SatorIndex(sator));

        SkillResponse response = await handler.HandleAsync(
            SongIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a real code collision must still play through the threaded gate");
    }

    // ---------------------------------------------------------------
    // FindSong (keywords miss)
    // ---------------------------------------------------------------

    private FindSongIntentHandler CreateFindSongHandler(IArtistIndex artistIndex)
        => new FindSongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: artistIndex,
            songNgramIndex: new TestHelpers.FakeSongIndex());

    [Fact]
    public async Task FindSong_KanaKeywords_StrictPlainFuzzyArtist_NoMatchRepromptNeverPlays()
    {
        // 'サト' as the keywords answer: the song search misses, the fallback
        // sees 'sato' vs Sator at the strict 90 with no collision (ST vs STR).
        // The threaded flag refuses the wrong-artist auto-play the inert flag
        // allowed (FindSong wires no JF-363 band, so the strict bar IS the
        // leak here); the honest outcome is the FindSongNoMatch re-prompt.
        SetupEmptyLibrary();
        var handler = CreateFindSongHandler(SatorIndex(Sator()));

        var attrs = FindSongAttributes(new FindSongSessionData { State = FindSongState.AwaitingKeywords, Keywords = "stale" });
        SkillResponse response = await handler.HandleAsync(
            KeywordsAnswer("サト"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession != true, "the honest outcome is the FindSongNoMatch re-prompt (an open Ask)");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("FindSongNoMatch", "ja-JP"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindSong_KanaKeywords_InBandPlainFuzzyArtist_NoMatchRepromptNeverPlays()
    {
        // The live bait through FindSong's wiring: ビートルズ -> 'bitoruzu' vs
        // Sator at 60. FindSong wires no JF-363 band, so the flag-false outcome
        // here was already null; the pin holds the CONTRACT that a kana-origin
        // keywords miss never surfaces an artist, whatever band the matcher
        // lands in.
        SetupEmptyLibrary();
        var handler = CreateFindSongHandler(SatorIndex(Sator()));

        var attrs = FindSongAttributes(new FindSongSessionData { State = FindSongState.AwaitingKeywords, Keywords = "stale" });
        SkillResponse response = await handler.HandleAsync(
            KeywordsAnswer("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession != true, "the honest outcome is the FindSongNoMatch re-prompt (an open Ask)");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("FindSongNoMatch", "ja-JP"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // PlayAlbum (the third leaking caller, found verifying the caller map)
    // ---------------------------------------------------------------

    private PlayAlbumIntentHandler CreateAlbumHandler(IArtistIndex artistIndex)
        => new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: artistIndex);

    [Fact]
    public async Task PlayAlbum_KanaAlbumTitle_InBandPlainFuzzySator_HonestAlbumNotFound_NeverSatorOffer()
    {
        // The offer must be reachable for this null-assertion to prove the
        // bar refused it: pin Confirm so the global default cannot mask a leak.
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        // PlayAlbum romanizes its album slot in place at entry and hands the
        // gate the Latin local; the task's caller map asserted raw text, but
        // the code passes 'bitoruzu' with the flag inert, so the JF-363 ALBUM
        // band (MediaTypeAlbum decline contract) offered Sator for a kana
        // album miss. The threaded album-slot flag takes the honest album
        // not-found.
        SetupEmptyLibrary();
        var handler = CreateAlbumHandler(SatorIndex(Sator()));

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the album not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundAlbumByName", "ja-JP", "bitoruzu"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
