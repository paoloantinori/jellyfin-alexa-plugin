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

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayVideoIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public PlayVideoIntentHandlerTests()
    {
        _fx.UserManager
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private sealed class RecordingPlayVideoHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory)
        : PlayVideoIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory)
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
        ILoggerFactory loggerFactory)
        : PlayVideoIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory)
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
            _fx.LoggerFactory);
    }

    private static IntentRequest CreatePlayVideoRequest(string? title = "The Matrix")
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
            }
        };
    }

    private static BaseItem CreateTestItem(string name, Guid? id = null)
    {
        var item = new Movie { Name = name, Id = id ?? Guid.NewGuid() };
        return item;
    }

    [Theory]
    [InlineData("PlayVideoIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
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
            _fx.LoggerFactory);

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
    /// The launch site is wired through BaseHandler.GetVideoAppLaunchUrl: an EAC3
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
