using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-865 pins for the cross-media VIDEO leg (CrossMediaFallback.TryVideoFallbackAsync,
/// wired at the PlaySong miss path after the artist and album cascades): the
/// maintainer's JF-844 decision (handler-side multi-medium rescue for the bare
/// "play &lt;movie&gt;" NLU steals, the JF-864 probe map's en-US/de/es-US/fr-CA shape).
/// Pins: the per-mode contract (Confirm default offers the single best video for
/// yes/no, AutoServe launches with the FoundVideoInstead announcement, Off keeps
/// today's not-found byte-identical), the word-count guard application, the
/// video-disabled gate, the below-threshold honest miss, the own-medium-hit /
/// carrier-never-disambiguated invariant (a song hit wins even when a same-named
/// movie exists), the cascade precedence (the artist offer wins the overlap), the
/// Movie+Episode query shape, and the JF-363 plumbing reuse end to end (the offer's
/// session state routes "yes" through YesIntentHandler's video leg and "no" through
/// NoIntentHandler's song-not-found decline).
/// </summary>
[Collection("Plugin")]
public class CrossMediaVideoFallbackTests : PluginTestBase
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

    /// <summary>
    /// The video-leg query predicate the mocks and assertions share (the simplify
    /// fold of the five inline copies): a query whose IncludeItemTypes covers the
    /// Movie/Episode kind set the leg searches.
    /// </summary>
    private static bool IsVideoQuery(InternalItemsQuery q)
        => q.IncludeItemTypes != null
            && q.IncludeItemTypes.Any(t => t == BaseItemKind.Movie || t == BaseItemKind.Episode);

    /// <summary>
    /// The full song-miss shape: every song, artist, and album query returns nothing
    /// (the caller only reaches the video leg after those cascades missed), while
    /// every Movie/Episode query returns <paramref name="videos"/> (recorded for the
    /// query-shape pins).
    /// </summary>
    private void SetupSongMissWithVideos(params BaseItem[] videos)
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

                // Song search, artist chain, and album cascade: all miss.
                return new List<BaseItem>();
            });
    }

    private static string NotFoundSongByNameEnUs(string query)
        => ResponseStrings.Get("NotFoundSongByName", "en-US", query);

    // ============================================================
    // Per-mode contract
    // ============================================================

    [Fact]
    public async Task PlaySong_Miss_MovieMatch_ConfirmDefault_OffersVideoAsk()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);

        // The code default IS Confirm; set it explicitly so the pin names its subject.
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Confirm: Ask (session open), no playback directive, the video in disambig
        // state under the VIDEO discriminator so the existing yes-leg plays it.
        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Nemo", speech);
        Assert.Contains("video", speech, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.SessionAttributes);
        Assert.Equal(DisambiguationHelper.MediaTypeVideo, response.SessionAttributes["disambig_type"]);
        Assert.Contains(movie.Id.ToString(), response.SessionAttributes["disambig_matches"]?.ToString() ?? string.Empty);

        // The JF-363 decline keys, song-flavored: "no" speaks today's song not-found.
        Assert.Equal("nemo", response.SessionAttributes?["crossmedia_notfound_query"]);
        Assert.Equal(DisambiguationHelper.MediaTypeSong, response.SessionAttributes?["crossmedia_notfound_type"]);
    }

    [Fact]
    public async Task PlaySong_Miss_MovieMatch_AutoServe_LaunchesMovieWithAnnouncement()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // AutoServe: the movie launches through the shared episode-launch chokepoint
        // (VideoApp.Launch here; the test context is capable by the null-interfaces
        // fail-open) with the FoundVideoInstead announcement riding the JF-501
        // progressive vehicle, so the final response carries the directive only.
        Assert.NotNull(response.Response?.Directives);
        Assert.Contains(response.Response.Directives!, d => d is VideoAppDirective.VideoAppLaunchDirective);
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Nemo"), "the AutoServe announce must speak the movie title");
    }

    [Fact]
    public async Task PlaySong_Miss_MovieMatch_AutoServe_WritesNowPlaying()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;

        var handler = new RecordingPlaySongHandler(_fx);
        var session = _fx.CreateSession();
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives!, d => d is VideoAppDirective.VideoAppLaunchDirective);
        Assert.Same(movie, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task PlaySong_Miss_MovieMatch_AutoServe_AnnounceOff_LaunchesSilently()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;
        _fx.Config.AnnounceCrossMediaSubstitution = false;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Contains(response.Response.Directives!, d => d is VideoAppDirective.VideoAppLaunchDirective);
        Assert.False(handler.Progressive.Contains("I found the video"), "announce off must not speak the substitution");
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task PlaySong_Miss_MovieMatch_Off_ReturnsTodaysNotFoundByteIdentical()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Off;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Off: today's not-found, byte-identical (the exact localized string, a
        // session-ending Tell, no offer state, no directive).
        Assert.True(response.Response?.ShouldEndSession);
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        Assert.Null(response.SessionAttributes?["disambig_type"]);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("nemo"), speech.Text);
    }

    // ============================================================
    // Gates: word-count guard, video flag, threshold
    // ============================================================

    [Fact]
    public async Task PlaySong_Miss_WordGuardThreeContentWords_NoVideoQuery()
    {
        // The shared 2-content-word guard applies to the video leg: a long query is a
        // poor movie guess, and the library must never even be asked.
        var movie = TestHelpers.CreateMovie("Pirates of the Caribbean");
        SetupSongMissWithVideos(movie);

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("pirates caribbean revenge"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("pirates caribbean revenge"), speech.Text);
        Assert.DoesNotContain(_videoQueries, IsVideoQuery);
    }

    [Fact]
    public async Task PlaySong_Miss_VideoDisabled_NoOfferNoQuery()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        // The gate reads Plugin.Instance?.Configuration FIRST (the live-read idiom),
        // so the flag is written on the LIVE config, not the injected one (the same
        // instance persists across the shared collection, so it is restored after).
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf865-video-off");
        bool savedVideoFlag = Plugin.Instance!.Configuration.VideoPlaybackEnabled;
        Plugin.Instance.Configuration.VideoPlaybackEnabled = false;
        try
        {
            var handler = new RecordingPlaySongHandler(_fx);
            var response = await handler.HandleAsync(
                CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

            // The flag gates BEFORE any query: an offered video the direct ask would
            // refuse can never be offered.
            var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
            Assert.Equal(NotFoundSongByNameEnUs("nemo"), speech.Text);
            Assert.Empty(_videoQueries);
        }
        finally
        {
            Plugin.Instance!.Configuration.VideoPlaybackEnabled = savedVideoFlag;
        }
    }

    [Fact]
    public async Task PlaySong_Miss_BelowThresholdMovie_HonestNotFound()
    {
        // "nemo" vs "Fargo" stays under the 85 bar: the JF-363 [normal, strict) band
        // is deliberately NOT extended to the video leg, so this is today's miss.
        var movie = TestHelpers.CreateMovie("Fargo");
        SetupSongMissWithVideos(movie);

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("nemo"), speech.Text);
    }

    // ============================================================
    // The carrier-never-disambiguated invariant + cascade precedence
    // ============================================================

    [Fact]
    public async Task PlaySong_SongHitWithSameTitledMovie_PlaysSong_NeverVideoOffer()
    {
        // The first-shot invariant: when the handler's OWN medium matches, the
        // cross-medium leg never runs (the carrier form stays the guaranteed
        // first-shot path; the own-medium hit is never disambiguated against video).
        var song = TestHelpers.CreateSong("Nemo");
        var movie = TestHelpers.CreateMovie("Nemo");
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (IsVideoQuery(q))
                {
                    _videoQueries.Add(q);
                    return new List<BaseItem> { movie };
                }

                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song };
                }

                return new List<BaseItem>();
            });

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Contains(response.Response!.Directives!, d => d is AudioPlayerPlayDirective);
        Assert.Null(response.SessionAttributes?["disambig_type"]);
        Assert.DoesNotContain(_videoQueries, IsVideoQuery);
    }

    [Fact]
    public async Task PlaySong_Miss_ArtistSubStrictAndMovieBothMatch_ArtistOfferWins()
    {
        // Cascade precedence: song, then artist, then album, then video. In the
        // artist band's overlap the artist offer wins and the movie is never
        // offered (the same precedence the album cascade documents).
        var artistId = Guid.NewGuid();
        var artistSong = TestHelpers.CreateSong("The Idiot Kings");
        var movie = TestHelpers.CreateMovie("soul coffin");
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (IsVideoQuery(q))
                {
                    _videoQueries.Add(q);
                    return new List<BaseItem> { movie };
                }

                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Soul Coughing", Id = artistId } };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { artistSong };
                }

                return new List<BaseItem>();
            });
        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = new RecordingPlaySongHandler(_fx);
        var response = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?["disambig_type"]);
        Assert.Contains("Soul Coughing", TestHelpers.GetSpeechText(response));
    }

    // ============================================================
    // Query shape
    // ============================================================

    [Fact]
    public async Task PlaySong_Miss_VideoQuery_CoversMoviesAndEpisodes_WithSearchTermFirst()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);

        var handler = new RecordingPlaySongHandler(_fx);
        await handler.HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // "and episodes if cheap": the leg queries BOTH kinds, and the tier-1 query
        // is the indexed SearchTerm shape PlayVideo's own primary search runs.
        Assert.NotEmpty(_videoQueries);
        Assert.Contains(_videoQueries, q => q.SearchTerm == "nemo"
            && q.IncludeItemTypes != null
            && q.IncludeItemTypes.Contains(BaseItemKind.Movie)
            && q.IncludeItemTypes.Contains(BaseItemKind.Episode));
    }

    // ============================================================
    // The JF-363 plumbing reuse, end to end
    // ============================================================

    [Fact]
    public async Task VideoOffer_YesIntent_PlaysTheOfferedMovie()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.LibraryManager.Setup(l => l.GetItemById(movie.Id)).Returns(movie);

        var offer = await new RecordingPlaySongHandler(_fx).HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);
        Assert.Equal(DisambiguationHelper.MediaTypeVideo, offer.SessionAttributes?["disambig_type"]);

        var yesHandler = new YesIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
            _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var response = await yesHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonYes }, Locale = "en-US", RequestId = "yes-req" },
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), offer.SessionAttributes, CancellationToken.None);

        // The offer's video state routes through the EXISTING yes-leg: the launch
        // with shouldEndSession omitted (the VideoApp contract).
        Assert.Contains(response.Response!.Directives!, d => d is VideoAppDirective.VideoAppLaunchDirective);
        Assert.Null(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task VideoOffer_NoIntent_DeclinesToTodaysSongNotFound()
    {
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);

        var offer = await new RecordingPlaySongHandler(_fx).HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        var noHandler = new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var response = await noHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "en-US", RequestId = "no-req" },
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), offer.SessionAttributes, CancellationToken.None);

        // The decline speaks today's song not-found (byte-identical to the pre-leg
        // miss), never the generic "no more matches".
        Assert.True(response.Response?.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Equal(NotFoundSongByNameEnUs("nemo"), speech.Text);
    }

    [Fact]
    public async Task VideoOffer_YesIntent_VideoDisabledMidPrompt_AnswersFeatureDisabled()
    {
        // JF-611 confirm-must-match-ask: an admin disabling video after the offer
        // opened must not let the confirm launch what the direct ask now refuses.
        var movie = TestHelpers.CreateMovie("Nemo");
        SetupSongMissWithVideos(movie);
        _fx.LibraryManager.Setup(l => l.GetItemById(movie.Id)).Returns(movie);

        var offer = await new RecordingPlaySongHandler(_fx).HandleAsync(
            CreateSongIntent("nemo"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // The live read goes through Plugin.Instance.Configuration (the IfFeatureDisabled
        // gate); the shared instance may pre-date this test, so the flag is written on
        // the LIVE config, the ContentAccessTests pattern.
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf865-video-off");
        bool savedVideoFlag = Plugin.Instance!.Configuration.VideoPlaybackEnabled;
        Plugin.Instance.Configuration.VideoPlaybackEnabled = false;
        try
        {
            var yesHandler = new YesIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
            var response = await yesHandler.HandleAsync(
                new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonYes }, Locale = "en-US", RequestId = "yes-req" },
                _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), offer.SessionAttributes, CancellationToken.None);

            Assert.True(response.Response?.ShouldEndSession);
            Assert.DoesNotContain(response.Response!.Directives ?? new List<IDirective>(), d => d is VideoAppDirective.VideoAppLaunchDirective);
            var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
            Assert.Equal(ResponseStrings.Get("FeatureDisabled", "en-US"), speech.Text);
        }
        finally
        {
            Plugin.Instance!.Configuration.VideoPlaybackEnabled = savedVideoFlag;
        }
    }
}
