using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Alexa.NET.Assertions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayVideoIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public PlayVideoIntentHandlerTests()
    {
        _fx.UserManager
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
    }

    private sealed class RecordingPlayVideoHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        PlaySongIntentHandler playSongHandler)
        : PlayVideoIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory, playSongHandler)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    /// <summary>
    /// Simulates a FAILED progressive send (2s timeout, auth rejection): the override is
    /// still invoked (the send was attempted) but reports failure, as the production
    /// method does on any failure.
    /// </summary>
    private sealed class FailingProgressiveHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        PlaySongIntentHandler playSongHandler)
        : PlayVideoIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory, playSongHandler)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
        {
            Progressive.Record(context, request, message);
            return Task.FromResult(false);
        }
    }

    private RecordingPlayVideoHandler CreateHandler()
    {
        return new RecordingPlayVideoHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            CreateSongHandler());
    }

    // The fixture's shared builder (JF-882): one construction site over the
    // class's own configured fixture.
    private PlaySongIntentHandler CreateSongHandler() => _fx.CreateSongHandler();

    private static IntentRequest CreatePlayVideoRequest(string? title = "The Matrix", string? dialogState = null)
    {
        var slots = new Dictionary<string, Slot>();
        if (title != null)
        {
            slots["title"] = new Slot { Value = title };
        }

        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = slots
            },
            DialogState = dialogState
        };
    }

    private static BaseItem CreateTestItem(string name, Guid? id = null)
    {
        var item = new Movie { Name = name, Id = id ?? Guid.NewGuid() };
        return item;
    }

    [Theory]
    [InlineData("PlayVideoIntent", true)]
    [InlineData("PlayMovieIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    // ---- JF-874: the PlayMovieIntent catalog-slot read ----

    private static IntentRequest CreatePlayMovieRequest(Slot? movieSlot)
    {
        var slots = new Dictionary<string, Slot>();
        if (movieSlot != null)
        {
            slots["movie"] = movieSlot;
        }

        return new IntentRequest
        {
            Intent = new Intent { Name = "PlayMovieIntent", Slots = slots }
        };
    }

    [Fact]
    public async Task Handle_MovieSlotCanonicalWinsOverRaw()
    {
        // JF-659 contract on the MovieTitle catalog slot: the ER canonical (the
        // exact library name the catalog synced) feeds the search, not the raw
        // spoken form.
        var movie = CreateTestItem("Sunny Dancer");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "Sunny Dancer" ? new List<BaseItem> { movie } : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("sunny dancer", "Sunny Dancer", slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Equal("Sunny Dancer", queries[0].SearchTerm);
    }

    [Fact]
    public async Task Handle_MovieSlotRawFallback_PlaysNonCatalogTitle()
    {
        // The catalog gate does not hard-block every miss: a carrier-anchored
        // wrapper whose title matches no catalog value still arrives with the
        // span raw-filled (live probe 2026-10-12, movie='ratatouille' against a
        // catalog lacking it). The raw path must keep playing it.
        var movie = CreateTestItem("Ratatouille");
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "ratatouille" ? new List<BaseItem> { movie } : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("ratatouille", canonical: null, slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
    }

    // ---- JF-876: the song-carrier misroute guard ----

    [Fact]
    public async Task Handle_MovieSlotSongCarrier_DelegatesToSongPath()
    {
        // The JF-876 steal shape: a title that is BOTH a song and an exact movie
        // catalog match ("metti il brano sunny dancer"), where the movie ER pull
        // selects PlayMovieIntent. The carrier noun in the raw fill must route
        // the ask to the SONG path (with its JF-871 miss cascade), never to the
        // movie search, even when the ER canonical names the movie exactly.
        var song = new Audio { Name = "Sunny Dancer", Id = Guid.NewGuid() };
        var movie = CreateTestItem("Sunny Dancer");
        var movieQueries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(movieQueries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "sunny dancer"
                ? (q.IncludeItemTypes.Contains(BaseItemKind.Audio)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem> { movie })
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("il brano sunny dancer", "Sunny Dancer", slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // The song played through the AudioPlayer path; no video launch happened.
        var play = Assert.Single(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        // No movie-shaped query was ever issued.
        Assert.DoesNotContain(movieQueries, q => q.IncludeItemTypes.Contains(BaseItemKind.Movie));
    }

    [Fact]
    public async Task Handle_SongCarrierWholeSpanTitleEscape_PlaysMovie()
    {
        // Code-review F1: a movie whose TITLE begins with a song-carrier phrase
        // ("The Song of Bernadette") must stay playable: when the raw fill IS
        // the canonical catalog title verbatim, the leading noun is the title,
        // not a misroute carrier.
        var movie = CreateTestItem("The Song of Bernadette");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => string.Equals(q.SearchTerm, "The Song of Bernadette", StringComparison.OrdinalIgnoreCase)
                ? new List<BaseItem> { movie }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("the song of bernadette", "The Song of Bernadette", slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
    }

    [Fact]
    public async Task Handle_SongCarrierDelegation_StripsExactlyOnce()
    {
        // Code-review F2: the RAW fill rides the synthesized song slot, so
        // PlaySong's single-cut strip owns the normalization. A song titled
        // "Canzone del Sole" behind "il brano canzone del sole" is searched as
        // "canzone del sole", never double-cut to "del sole".
        var song = new Audio { Name = "Canzone del Sole", Id = Guid.NewGuid() };
        var searchTerms = new List<string?>();
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns((InternalItemsQuery q) => string.Equals(q.SearchTerm, "canzone del sole", StringComparison.OrdinalIgnoreCase)
                ? new List<BaseItem> { song }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("il brano canzone del sole", canonical: null, slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.Contains("canzone del sole", searchTerms);
        Assert.DoesNotContain("del sole", searchTerms);
        Assert.Single(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
    }

    [Fact]
    public async Task Handle_MovieCarrierStillPlaysMovie()
    {
        // The movie carrier ("il film X", the JF-509 fill-swallow shape) must
        // keep playing the movie: the guard is leading song nouns only.
        var movie = CreateTestItem("Sunny Dancer");
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "il film sunny dancer"
                ? new List<BaseItem> { movie }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(new Slot { Name = "movie", Value = "il film sunny dancer" }),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
    }

    // ---- JF-882 (review F6): the free-text title's miss-tail delegation ----

    [Fact]
    public async Task Handle_FreeTextTitleSongCarrier_VideoMiss_DelegatesToSongPath()
    {
        // The F6 residual: a song ask NLU-steered to PlayVideoIntent carries
        // title='il brano X', runs the video search on the unstripped fill, and
        // would answer NotFoundVideo. On the confirmed miss the ask must
        // delegate to the song path instead (the JF-876 contract; the raw fill
        // rides the song slot, PlaySong strips the carrier once).
        var song = new Audio { Name = "Sunny Dancer", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => string.Equals(q.SearchTerm, "sunny dancer", StringComparison.OrdinalIgnoreCase)
                && q.IncludeItemTypes.Contains(BaseItemKind.Audio)
                ? new List<BaseItem> { song }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("il brano sunny dancer"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var play = Assert.Single(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
    }

    [Fact]
    public async Task Handle_FreeTextTitleCarrierTitledMovie_RawHit_PlaysMovie()
    {
        // The raw-first discipline (the slot has no ER canonical, so the JF-876
        // whole-span escape is unavailable): a movie genuinely titled with a
        // leading song carrier is found by the RAW query and plays as a movie;
        // the miss-tail delegation never runs on a hit.
        var movie = CreateTestItem("Il Brano di Bernadette");
        var queries = new List<InternalItemsQuery>();
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => string.Equals(q.SearchTerm, "il brano di bernadette", StringComparison.OrdinalIgnoreCase)
                ? new List<BaseItem> { movie }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("il brano di bernadette"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Empty(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
    }

    [Fact]
    public async Task Handle_FreeTextTitleWithoutCarrier_VideoMiss_KeepsNotFoundVideo()
    {
        // The delegation is carrier-gated: an ordinary free-text title that
        // misses everywhere keeps the honest NotFoundVideo Tell.
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("xyzzyfoo movie"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Contains("xyzzyfoo", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_MovieWholeSpanEscapeThatMisses_KeepsNotFoundVideo()
    {
        // The miss-tail delegation is the FREE-TEXT branch's alone: a
        // PlayMovieIntent arrival that survived the entry guard's whole-span
        // escape (fill IS the canonical title verbatim) is a VERIFIED movie ask,
        // and its confirmed miss keeps the honest NotFoundVideo Tell instead of
        // delegating to the song path.
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot("the song of bernadette", "The Song of Bernadette", slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Contains("bernadette", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_SongCarrierWholeSpanTitleEscape_TrimsFill_PlaysMovie()
    {
        // Code-review round 2: detection trims the fill, so the escape compare
        // must trim too; a whole-span title fill with leading/trailing
        // whitespace (the documented whitespace-fill shape) escapes instead of
        // delegating to the song path.
        var movie = CreateTestItem("The Song of Bernadette");
        _fx.SetupUserMock();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => string.Equals(q.SearchTerm?.Trim(), "the song of bernadette", StringComparison.OrdinalIgnoreCase)
                ? new List<BaseItem> { movie }
                : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(TestHelpers.ResolvedSlot(" the song of bernadette ", "The Song of Bernadette", slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Empty(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
    }

    [Fact]
    public async Task Handle_MovieSlotEmpty_ElicitsThroughPlayVideoRegistration()
    {
        // The JF-874 delegation: PlayMovieIntent never becomes an elicit target
        // (an ElicitSlots entry would force all 17 locales' dialog sections);
        // the empty-slot miss elicits the registered PlayVideo title slot, so
        // the follow-up arrives as PlayVideoIntent through the shared handler.
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayMovieRequest(new Slot { Name = "movie", Value = " " }),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var elicit = Assert.Single(response.Response.Directives, d => d.Type == "Dialog.ElicitSlot");
        var directive = Assert.IsType<ElicitSlotDirective>(elicit);
        Assert.Equal("PlayVideoIntent", directive.UpdatedIntent.Name);
        Assert.Equal("title", directive.SlotToElicit);
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Handle_NoTitleSlot_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        var response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);

        // 2026-09-23 review: lock the ELICIT shape, not just the prompt text (the
        // text is byte-identical to the old dead-mic Tell; the shape is the fix).
        Assert.Contains(response.Response.Directives, d => d.Type == "Dialog.ElicitSlot");
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(""),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
        Assert.Contains(response.Response.Directives, d => d.Type == "Dialog.ElicitSlot");
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Handle_CancelWordDuringOpenElicit_EndsFlow()
    {
        // 2026-09-23 review: the elicit conversion joined the trap regime; the
        // shared hatch must fire (JF-550/JF-620), not search a movie named "stop".
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("stop", dialogState: "IN_PROGRESS"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Equal(ResponseStrings.Get("FlowCancelled", "en-US"), speech.Text);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Handle_FoundEpisodeOnScreenlessDevice_DegradesToAudioPlayer()
    {
        // JF-587: an episode hit on a screenless context (the Echo Dot shape) takes
        // the audio-only AudioPlayer launch, not the screen-required refusal.
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ep. Pilot",
            Guid.NewGuid(),
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });
        _fx.SetupUserMock();

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Ep. Pilot"),
            TestHelpers.CreateScreenlessContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var play = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_NoResults_ReturnsNotFound()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Unknown Movie"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_ReturnsVideoAppDirective()
    {
        var movie = CreateTestItem("The Matrix");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // JF-349: a fresh video launch (no resume position) announces the title instead of
        // launching silently, matching PlayRandom/PlayEpisode. Resume-position launches still use
        // the "ResumingVideo" speech (unchanged if-branch). JF-501: the announce rides the
        // progressive-response vehicle, so the final launch response carries the directive ONLY.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("The Matrix"), "progressive announce must speak the movie title");
        response.HasDirective<VideoAppLaunchDirective>();
    }

    /// <summary>
    /// JF-501: with the announce toggle OFF the launch must keep today's silent shape:
    /// no progressive announce (only the SearchingMedia ping may arrive) and no
    /// OutputSpeech on the final response.
    /// </summary>
    [Fact]
    public async Task Handle_AnnounceOff_SendsNoProgressiveAnnounceAndNoOutputSpeech()
    {
        var movie = CreateTestItem("The Matrix");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var user = TestHelpers.CreateTestUser();
        user.AnnounceNowPlaying = false;

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            user,
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.False(handler.Progressive.Contains("The Matrix"), "announce off must not send a progressive announce");
    }

    /// <summary>
    /// JF-501 failure fallback: a FAILED progressive send (2s timeout, auth rejection,
    /// network error) must not lose the announce. SendProgressiveResponse reports false
    /// and the announce rides the final response's OutputSpeech (the pre-JF-501 shape);
    /// before the Task&lt;bool&gt; fallback a swallowed failure dropped the announce entirely.
    /// </summary>
    [Fact]
    public async Task Handle_ProgressiveSendFails_AnnounceRidesFinalResponse()
    {
        var movie = CreateTestItem("The Matrix");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = new FailingProgressiveHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            CreateSongHandler());

        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        // The fresh-launch announce is SSML; on fallback the SAME announce object rides
        // the final response instead of being dropped.
        var speech = Assert.IsType<SsmlOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("The Matrix", speech.Ssml);
        Assert.True(handler.Progressive.Contains("The Matrix"), "the send must have been attempted (and reported failure) before the fallback");
    }

    [Fact]
    public async Task Handle_TitleSwallowedMediaNoun_StripsAndFinds()
    {
        // JF-509: the SearchQuery fill drifts and can swallow the carrier noun
        // ('voglio guardare il film ada' filled title='film ada' on-device
        // 2026-09-06, corr=655db7c3). RAW-FIRST contract: the raw title query
        // runs first and misses, then ONE stripped retry finds 'ada'.
        var movie = CreateTestItem("Ada: My Mother the Architect");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "ada" ? new List<BaseItem> { movie } : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("film ada"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        // The raw query runs first; the stripped retry lands after the fuzzy miss.
        Assert.Equal("film ada", queries[0].SearchTerm);
        Assert.Contains(queries, q => q.SearchTerm == "ada");
    }

    [Fact]
    public async Task Handle_RawTitleWinsWhenItExists()
    {
        // JF-509 raw-first: a movie genuinely titled starting with the noun
        // ('Film Stars Don't Die in Liverpool') is found by the RAW query; the
        // strip retry never fires.
        var movie = CreateTestItem("Film Stars Don't Die in Liverpool");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "Film Stars Don't Die in Liverpool" ? new List<BaseItem> { movie } : new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Film Stars Don't Die in Liverpool"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Single(queries.Where(q => q.SearchTerm != null));
    }

    [Fact]
    public async Task Handle_TitleWordFragment_NotStripped()
    {
        // JF-509: the trailing-space word-fragment guard ('filmstar' is one word,
        // not the noun + a title): the strip returns null, no second query.
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("filmstar"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text, StringComparison.OrdinalIgnoreCase);
        // One-word fragment: the strip returns null (nothing new to retry), so the
        // only search-shaped query is the raw one (plus the fuzzy fallback shape).
        Assert.DoesNotContain(queries, q => q.SearchTerm == "star");
    }

    [Fact]
    public async Task Handle_TitleOnlyMediaNoun_NoRetry()
    {
        // The strip leaves nothing searchable ('il film' is all noun): the null
        // return means no retry, and the not-found names the raw value the user said.
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("il film"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // Raw + the fuzzy fallback shape only: no stripped-value query exists.
        Assert.DoesNotContain(queries, q => q.SearchTerm != null && !q.SearchTerm.Contains(' '));
        Assert.Contains("il film", Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_FoundMultipleResults_ReturnsDisambiguationPrompt()
    {
        var movie1 = CreateTestItem("Inception");
        var movie2 = CreateTestItem("Interstellar");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie1, movie2 });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Nolan"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.False(response.Response.ShouldEndSession);

        string speechText = response.Response.OutputSpeech is SsmlOutputSpeech ssml
            ? ssml.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;

        // With default Confirm behavior, should get "did you mean?" prompt suggesting the closest match
        Assert.True(
            speechText.Contains("Inception", StringComparison.Ordinal) ||
            speechText.Contains("Interstellar", StringComparison.Ordinal),
            "Expected a fuzzy suggestion for one of the candidate movies");
    }

    [Fact]
    public async Task Handle_NullTitleSlotValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(null),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task Handle_WhitespaceTitle_ReturnsPrompt(string title)
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(title),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_DirectiveContainsSourceAndMetadata()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.Contains(id.ToString(), directive.VideoItem.Source);
        Assert.Contains("Videos", directive.VideoItem.Source);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public async Task Handle_FoundMovie_SetsSessionQueue()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);
        var session = _fx.CreateSession();

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        await handler.HandleAsync(CreatePlayVideoRequest("The Matrix"), _fx.CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_VideoResponse_ShouldEndSessionIsNull()
    {
        var movie = CreateTestItem("Test Video");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Test Video"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // VideoApp.Launch must NOT include shouldEndSession; Alexa rejects it
        Assert.Null(response.Response.ShouldEndSession);
    }

    // ========== JF-498: codec-routed static-vs-HLS launch source ==========

    /// <summary>
    /// The launch site is wired through PlaybackLaunchBuilder.GetVideoAppLaunchUrl: an EAC3
    /// movie (the evidenced library shape) launches the HLS REMUX endpoint, not the
    /// static stream the Echo cannot decode.
    /// </summary>
    [Fact]
    public async Task Handle_Eac3Movie_LaunchesEpisodeHlsRemux()
    {
        var id = Guid.NewGuid();
        var movie = new TestHelpers.TestMovieWithStreams(
            "Adolescence S01E01",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Adolescence"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/stream.m3u8?token=", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Videos/", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compatible shape (h264 + aac) keeps the static stream URL: zero change
    /// for sources that already play.
    /// </summary>
    [Fact]
    public async Task Handle_AacMovie_KeepsStaticVideoStream()
    {
        var id = Guid.NewGuid();
        var movie = new TestHelpers.TestMovieWithStreams(
            "The Matrix",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains($"/Videos/{id}/stream?static=true&api_key=", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("video-audio", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-500: an HEVC-video movie (the Adolescence library shape) launches the
    /// episode HLS endpoint too: the endpoint re-probes and runs the video
    /// TRANSCODE tier (H.264 re-encode) instead of the remux. The first cut kept
    /// these on the warned static URL, which never started on the Echo Show.
    /// </summary>
    [Fact]
    public async Task Handle_HevcMovie_LaunchesEpisodeHlsTranscode()
    {
        var id = Guid.NewGuid();
        var movie = new TestHelpers.TestMovieWithStreams(
            "Adolescence S01E02",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "hevc"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Adolescence"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/stream.m3u8?token=", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Videos/", directive.VideoItem.Source, StringComparison.Ordinal);
    }
}
