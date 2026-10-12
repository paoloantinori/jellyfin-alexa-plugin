using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-871 pins for the PlaySong fuzzy-suggestion interception fix: the
/// "intendevi X?" near-hit prompt (the JF-116 class, HandleFuzzyMiss's Confirm
/// branch) used to preempt every later cross-media tier, so an EXACT-title film
/// ("brano sunny dancer" with the movie in the library) was unreachable on the
/// first turn and a declined suggestion dead-ended at "no more matches".
/// Pins: the ranking (an exact-title video offered BEFORE the sub-threshold
/// song near-hit prompt; a fuzzy-only video does NOT preempt; suggestion Off
/// keeps the prompt), the decline continuation ("no" to the suggestion runs the
/// remaining artist/album/video cascade and ends in the honest song not-found
/// when everything misses), the no-musician scope (the decline context is
/// written only for the cascade shape), the null-collaborator fallback, and the
/// flow supersession (a newer ask marks the decline key for removal while the
/// suggestion ask keeps it live).
/// </summary>
[Collection("Plugin")]
public class SongSuggestionVideoPreemptionTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new();
    private readonly List<InternalItemsQuery> _videoQueries = new();

    private sealed class RecordingPlaySongHandler : PlaySongIntentHandler
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        public RecordingPlaySongHandler(HandlerTestFixture fx)
            : base(
                fx.SessionManager.Object,
                fx.Config,
                fx.LibraryManager.Object,
                fx.UserManager.Object,
                fx.UserDataManager.Object,
                fx.LoggerFactory)
        {
        }

        protected override Task<bool> SendProgressiveResponse(Context context, Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private static IntentRequest CreateSongIntent(string song, string locale = "en-US")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static bool IsVideoQuery(InternalItemsQuery q)
        => q.IncludeItemTypes != null
            && q.IncludeItemTypes.Any(t => t == BaseItemKind.Movie || t == BaseItemKind.Episode);

    /// <summary>
    /// The intendevi-interception shape: the song SearchTerm tier returns the
    /// near-hit songs (so PlaySong reaches its multi-match fuzzy block instead of
    /// the miss cascade), every other music query misses, and video queries return
    /// <paramref name="videos"/> (recorded).
    /// </summary>
    private void SetupNearHitSongsAndVideos(List<BaseItem> songs, List<BaseItem> videos, List<BaseItem>? artists = null)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (IsVideoQuery(q))
                {
                    _videoQueries.Add(q);
                    return new List<BaseItem>(videos);
                }

                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem>(artists ?? new List<BaseItem>());
                }

                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem>(songs);
                }

                // Album cascade, everything else: miss.
                return new List<BaseItem>();
            });
    }

    private static string NotFoundSongByNameEnUs(string query)
        => ResponseStrings.Get("NotFoundSongByName", "en-US", query);

    /// <summary>
    /// Null-safe session-attribute read: the pins assert ABSENCE on dictionaries
    /// that exist (the indexer throws on a missing key, null-conditional or not).
    /// </summary>
    private static string? GetAttr(SkillResponse response, string key)
        => response.SessionAttributes?.GetValueOrDefault(key) as string;

    // ============================================================
    // Ranking: the exact-title video preempts the intendevi prompt
    // ============================================================

    [Fact]
    public async Task NearHitSong_NoExactVideo_StillSuggestsTheSong()
    {
        // The honest near-miss guard: "sunni" intending the Bobby Hebb song, with
        // no exact-titled video, keeps the song suggestion (the JF-116 contract).
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var videos = new List<BaseItem> { TestHelpers.CreateMovie("Fargo") };
        SetupNearHitSongsAndVideos(songs, videos);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("sunni"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The intendevi ask: open session, the song in disambig state under the
        // SONG discriminator, the near-hit name spoken, and the JF-871 decline
        // context carried for the "no" turn.
        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.Equal(DisambiguationHelper.MediaTypeSong, response.SessionAttributes?["disambig_type"]);
        Assert.Contains("Sunny", TestHelpers.GetSpeechText(response));
        Assert.Equal("sunni", GetAttr(response, DisambiguationHelper.AttrSongSuggestionQuery));
    }

    [Fact]
    public async Task ExactTitleVideo_PreemptsTheSongSuggestion()
    {
        // The JF-871 incident shape: "sunny dancer" (an exact-title film, no song
        // of that name) fuzzy-matches the song "Sunny"; the EXACT video offer must
        // come FIRST, not after a confirmed miss that never happens.
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var videos = new List<BaseItem> { TestHelpers.CreateMovie("Sunny Dancer") };
        SetupNearHitSongsAndVideos(songs, videos);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("sunny dancer"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The JF-865 video offer ask (Confirm default): the video in disambig
        // state under the VIDEO discriminator, the song-flavored decline keys, and
        // NO intendevi song prompt (the song decline context is never written).
        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.Equal(DisambiguationHelper.MediaTypeVideo, response.SessionAttributes?["disambig_type"]);
        Assert.Contains("Sunny Dancer", TestHelpers.GetSpeechText(response));
        Assert.Contains("video", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sunny dancer", GetAttr(response, DisambiguationHelper.AttrCrossmediaQuery));
        Assert.Equal(DisambiguationHelper.MediaTypeSong, GetAttr(response, DisambiguationHelper.AttrCrossmediaType));
        Assert.Null(GetAttr(response, DisambiguationHelper.AttrSongSuggestionQuery));
    }

    [Fact]
    public async Task ExactTitleVideo_AutoServe_LaunchesInsteadOfPrompting()
    {
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var videos = new List<BaseItem> { TestHelpers.CreateMovie("Sunny Dancer") };
        SetupNearHitSongsAndVideos(songs, videos);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("sunny dancer"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // AutoServe: the exact video launches (the mode's never-prompt contract),
        // the song suggestion never fires.
        Assert.Contains(response.Response!.Directives!, d => d is VideoAppDirective.VideoAppLaunchDirective);
        Assert.True(handler.Progressive.Contains("Sunny Dancer"), "the AutoServe announce must speak the movie title");
    }

    [Fact]
    public async Task ExactTitleVideo_SuggestionOff_KeepsTheSongSuggestion()
    {
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var videos = new List<BaseItem> { TestHelpers.CreateMovie("Sunny Dancer") };
        SetupNearHitSongsAndVideos(songs, videos);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Off;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("sunny dancer"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Off gates the preemption at the video leg's entry: today's intendevi
        // prompt, byte-identical.
        Assert.Equal(DisambiguationHelper.MediaTypeSong, response.SessionAttributes?["disambig_type"]);
        Assert.Contains("Sunny", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task FuzzyOnlyVideo_DoesNotPreempt_ThenDeclineOffersIt()
    {
        // A non-exact video must NOT outrank the song suggestion on turn one
        // (exactOnly), but the DECLINE continues the cascade and the fuzzy video
        // tier recovers it there: the two-turn contract for near-title films.
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var videos = new List<BaseItem> { TestHelpers.CreateMovie("Sunny Dancer") };
        SetupNearHitSongsAndVideos(songs, videos);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var suggestion = await handler.HandleAsync(
            CreateSongIntent("sunny dancr"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Turn one: the song suggestion ("sunny dancr" is not the movie's exact
        // title, so the preemption declines to fire).
        Assert.Equal(DisambiguationHelper.MediaTypeSong, suggestion.SessionAttributes?["disambig_type"]);
        Assert.Equal("sunny dancr", GetAttr(suggestion, DisambiguationHelper.AttrSongSuggestionQuery));

        // Turn two: "no" declines the suggestion, the cascade's video tier finds
        // the near-title movie above the JF-865 bar and offers it.
        var noHandler = new NoIntentHandler(
            _fx.SessionManager.Object, _fx.Config,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        var declined = await noHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "en-US", RequestId = "no-req" },
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), suggestion.SessionAttributes, CancellationToken.None);

        Assert.True(declined.Response?.ShouldEndSession != true);
        Assert.Equal(DisambiguationHelper.MediaTypeVideo, declined.SessionAttributes?["disambig_type"]);
        Assert.Contains("Sunny Dancer", TestHelpers.GetSpeechText(declined));
        Assert.Equal("sunny dancr", GetAttr(declined, DisambiguationHelper.AttrCrossmediaQuery));
    }

    // ============================================================
    // The decline continuation
    // ============================================================

    [Fact]
    public async Task DeclinedSuggestion_NothingAnywhere_EndsInSongNotFound()
    {
        // The old dead-end spoke "no more matches"; the rejection is a song-tier
        // miss, so the honest terminal is the song not-found for the stored query.
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        SetupNearHitSongsAndVideos(songs, new List<BaseItem>());
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var suggestion = await handler.HandleAsync(
            CreateSongIntent("sunny dancr"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);
        Assert.Equal(DisambiguationHelper.MediaTypeSong, suggestion.SessionAttributes?["disambig_type"]);

        var noHandler = new NoIntentHandler(
            _fx.SessionManager.Object, _fx.Config,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        var declined = await noHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "en-US", RequestId = "no-req" },
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), suggestion.SessionAttributes, CancellationToken.None);

        Assert.True(declined.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(declined.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("sunny dancr"), speech.Text);
    }

    [Fact]
    public async Task PlainSongListAsk_CarriesNoDeclineContext()
    {
        // Scope pin: only the intendevi suggestion ask (HandleFuzzyMiss's Confirm
        // branch with sessionExtras) writes the decline context; the multi-match
        // list ask from the same handler does not, so its "no" keeps cycling the
        // list (today's behavior).
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Alpha Song"), TestHelpers.CreateSong("Beta Song") };
        SetupNearHitSongsAndVideos(songs, new List<BaseItem>());

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("xyzzy qwerty"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Null(GetAttr(response, DisambiguationHelper.AttrSongSuggestionQuery));
    }

    [Fact]
    public async Task MusicianScopedSuggestion_CarriesNoDeclineContext()
    {
        // The cascade legs run only for the no-musician shape, so the decline
        // context is written only there (the musician-scoped residual keeps
        // today's decline behavior).
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        var artist = new MediaBrowser.Controller.Entities.Audio.MusicArtist { Name = "Bobby Hebb", Id = Guid.NewGuid() };
        SetupNearHitSongsAndVideos(songs, new List<BaseItem>(), new List<BaseItem> { artist });
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var intent = TestHelpers.CreatePlaySongIntent(
            "sunny dancer", new Slot { Name = "musician", Value = "bobby hebb" }, "en-US");

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(intent, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The artist resolution finds nothing (mock), so the flow reaches the
        // fuzzy song block; the suggestion fires without the decline context and
        // without the video preemption (both are no-musician-only seams).
        Assert.Equal(DisambiguationHelper.MediaTypeSong, response.SessionAttributes?["disambig_type"]);
        Assert.Null(GetAttr(response, DisambiguationHelper.AttrSongSuggestionQuery));
        Assert.DoesNotContain(_videoQueries, IsVideoQuery);
    }

    [Fact]
    public async Task Decline_JaQuery_StoresPreRomanization_AndDeclineRomanizesIt()
    {
        // JF-871 kana mirror pin (code-review finding): the stored decline context
        // is the PRE-romanization query (the kana flag's capture point) and the
        // decline re-romanizes it, reproducing PlaySong's locals. The song names
        // are derived from the romanizer itself so the pin targets the plumbing,
        // not the romanization table.
        string katakana = "\u30B5\u30CB\u30FC \u30C0\u30F3\u30B5\u30FC"; // katakana for a two-word title
        string romanized = Jellyfin.Plugin.AlexaSkill.Alexa.Util.KatakanaRomanizer.Romanize(katakana);
        string firstToken = romanized.Split(' ')[0];
        var songs = new List<BaseItem>
        {
            TestHelpers.CreateSong(firstToken),
            TestHelpers.CreateSong(firstToken + " (remaster)")
        };
        SetupNearHitSongsAndVideos(songs, new List<BaseItem>());
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var suggestion = await handler.HandleAsync(
            CreateSongIntent(katakana, "ja-JP"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The intendevi fired (containment score, second token missing) and the
        // stored query is the RAW kana, not the romanized form.
        Assert.Equal(DisambiguationHelper.MediaTypeSong, suggestion.SessionAttributes?["disambig_type"]);
        Assert.Equal(katakana, GetAttr(suggestion, DisambiguationHelper.AttrSongSuggestionQuery));

        // The decline re-derives the locals: the honest miss speaks the ROMANIZED
        // query, exactly what PlaySong's own miss would speak.
        var noHandler = new NoIntentHandler(
            _fx.SessionManager.Object, _fx.Config,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        var declined = await noHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "ja-JP", RequestId = "no-req" },
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), suggestion.SessionAttributes, CancellationToken.None);

        Assert.True(declined.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(declined.Response!.OutputSpeech);
        Assert.Equal(
            ResponseStrings.Get("NotFoundSongByName", "ja-JP", romanized),
            speech.Text);
    }

    // ============================================================
    // Flow supersession (the JF-398 registry)
    // ============================================================

    [Fact]
    public async Task SuggestionAskKeepsDeclineKey_NewerAskSupersedesIt()
    {
        var songs = new List<BaseItem> { TestHelpers.CreateSong("Sunny"), TestHelpers.CreateSong("Sunny (remaster)") };
        SetupNearHitSongsAndVideos(songs, new List<BaseItem>());
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var suggestion = await handler.HandleAsync(
            CreateSongIntent("sunni"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The suggestion ask keeps its own decline key live: the removal marker
        // does not list it.
        var marker = suggestion.SessionAttributes?[SessionAttributeRemoval.MarkerKey] as System.Collections.IEnumerable;
        Assert.NotNull(marker);
        Assert.DoesNotContain(marker.Cast<object>(), k => k as string == DisambiguationHelper.AttrSongSuggestionQuery);

        // A newer disambig ask (the list ask for a different query in the same
        // session) supersedes it: the stale decline key is marked for removal, so
        // a later "no" can never run the cascade with the stale query.
        var listSongs = new List<BaseItem> { TestHelpers.CreateSong("Alpha Song"), TestHelpers.CreateSong("Beta Song") };
        SetupNearHitSongsAndVideos(listSongs, new List<BaseItem>());
        var listAsk = await handler.HandleAsync(
            CreateSongIntent("xyzzy qwerty"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        var listMarker = listAsk.SessionAttributes?[SessionAttributeRemoval.MarkerKey] as System.Collections.IEnumerable;
        Assert.NotNull(listMarker);
        Assert.Contains(listMarker.Cast<object>(), k => k as string == DisambiguationHelper.AttrSongSuggestionQuery);
    }

    // ============================================================
    // The miss cascade stays the honest terminal after the extraction
    // ============================================================

    [Fact]
    public async Task SongMiss_NothingAnywhere_StillNotFoundsCleanly()
    {
        SetupNearHitSongsAndVideos(new List<BaseItem>(), new List<BaseItem>());

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("xyzzy qwerty"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("xyzzy qwerty"), speech.Text);
    }
}
