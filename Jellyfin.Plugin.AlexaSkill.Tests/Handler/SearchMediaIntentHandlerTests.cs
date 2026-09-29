using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class SearchMediaIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture(configure: c => c.AsrCompoundWordFixEnabled = false);

    /// <summary>
    /// JF-501/JF-538 test seam (the PlayVideoIntentHandlerTests shape): subclasses the
    /// handler and overrides <c>SendProgressiveResponse</c> to capture progressive
    /// speech without network I/O. The video-launch announce rides the progressive
    /// vehicle, so video-path assertions read the capture instead of OutputSpeech.
    /// </summary>
    private sealed class RecordingSearchMediaHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory)
        : SearchMediaIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingSearchMediaHandler CreateHandler()
    {
        return new RecordingSearchMediaHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private SearchMediaIntentHandler CreateHandlerWithSongIndex(Mock<ISongNgramIndex> songIndex)
    {
        return new SearchMediaIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            songNgramIndex: songIndex.Object);
    }

    private static IntentRequest CreateIntentRequest(string? query = null)
    {
        var intent = new Intent { Name = IntentNames.SearchMedia };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (query != null)
        {
            intent.Slots["query"] = new global::Alexa.NET.Request.Slot { Name = "query", Value = query };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public void CanHandle_SearchMediaIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test song");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingQuery_ReturnsCouldNotUnderstand()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("understand", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SingleEpisodeResultOnScreenlessDevice_DegradesToAudioPlayer()
    {
        // JF-587: an episode result on a screenless context (the Echo Dot shape)
        // takes the audio-only AudioPlayer launch, not the screen-required refusal.
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Ep. Pilot");
        var context = TestHelpers.CreateScreenlessContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ep. Pilot",
            Guid.NewGuid(),
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var play = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!));
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_NoResults_ReturnsMediaNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SingleAudioResult_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Bohemian Rhapsody");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audio = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audio.Id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_SingleVideoResult_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Inception");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Inception",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // VideoApp.Launch must NOT include shouldEndSession
        Assert.Null(response.Response.ShouldEndSession);
        Assert.NotEmpty(response.Response.Directives);
        // JF-349 announced the title; JF-538 moves that announce onto the JF-501
        // progressive vehicle, so the final launch response carries the directive only.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Inception"), "progressive announce must speak the movie title");
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(movie, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_SingleVideoResult_WithProgress_AnnouncesResumePosition()
    {
        // C4: a half-watched movie found via search announces "Resuming X from Y"
        // (matching PlayVideo), not "Now playing".
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Inception");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Inception",
            Id = Guid.NewGuid()
        };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // JF-538: the "Resuming X" announce rides the progressive vehicle; the final
        // launch response carries the directive only.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Resuming"), "the resume announce must ride the progressive vehicle");
    }

    [Fact]
    public async Task HandleAsync_MultipleResults_ReturnsDisambiguation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Star Wars");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var item1 = new Audio
        {
            Name = "Star Trek Theme",
            Id = Guid.NewGuid()
        };

        var item2 = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Stargate",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item1, item2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    // JF-526 (JF-508 sibling): the site-level FuzzyMatch pre-check below returns
    // BEFORE HandleFuzzyMiss, so without the shared gate here a 2-word partial-coverage
    // hit ("soul coffee" -> "Starfish & Coffee", score 72) auto-played ungated.
    [Fact]
    public async Task HandleAsync_TwoWordPartialCoverageFuzzyHit_PromptsInsteadOfAutoPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "soul coffee");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Starfish & Coffee", Id = Guid.NewGuid() },
                new Audio { Name = "Coffee & TV", Id = Guid.NewGuid() }
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "the site-level pre-check must not auto-play a partial-coverage short query (JF-526)");
    }

    [Fact]
    public async Task HandleAsync_TwoWordFullCoverageFuzzyHit_StillAutoPlays()
    {
        // Counter-case: both query words covered by the best candidate, so the
        // pre-check keeps its auto-play (the gate withholds only unaccounted words).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "coffee tv");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Starfish & Coffee", Id = Guid.NewGuid() },
                new Audio { Name = "Coffee & TV", Id = Guid.NewGuid() }
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    // --- JF-654 review round 2: the kana bar on this handler's song auto-plays ---

    /// <summary>
    /// JF-654 review round 2, finding 1: the fuzzy pass scans the playable kinds
    /// INCLUDING songs, so its single-result pick is a song auto-play; a kana-origin
    /// query ('ビートルズ' -> 'bitoruzu') whose fuzzy hit is a Latin soup match
    /// ('Bitters Absolut') carries no length-banded Double Metaphone collision and
    /// no near-exact score, so the gate refuses it and the flow falls through to the
    /// (itself gated) song-title retry: the honest MediaNotFound, never a play.
    /// AutoPlay behavior + a floor threshold let the fuzzy pass RETURN the soup hit
    /// deterministically (AutoPlay bypasses SearchItemsFuzzyAsync's coverage gate).
    /// </summary>
    [Fact]
    public async Task HandleAsync_KanaQuery_FuzzyPassSoupHit_GatedToHonestNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "ビートルズ");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        user.FuzzyMatchThreshold = 10;
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var bait = new Audio { Name = "Bitters Absolut", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
                q.SearchTerm == null && q.NameStartsWith == null && q.NameContains == null
                    && q.ArtistIds == null && q.Limit == 500
                    ? new List<BaseItem> { bait }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the MediaNotFound Tell");
        Assert.True(response.Response.Directives?.All(d => d is not AudioPlayerPlayDirective) != false,
            "the soup fuzzy hit must never auto-play for a kana-origin query");
    }

    /// <summary>
    /// JF-654 review round 2, finding 3: the FuzzyMatch topMatch full-coverage
    /// pre-check is the fuzzy pass's sibling song auto-play, one branch later. The
    /// tribute-band shape ('Bitoruzu Tribute Act' covers the verbatim keyword but
    /// widens the title far past the length band, score 85 below the near-exact
    /// leg) is refused: the honest MediaNotFound rather than a fall-through into
    /// HandleFuzzyMiss, whose >= 90 auto-accept would play the very item the bar
    /// just refused.
    /// </summary>
    [Fact]
    public async Task HandleAsync_KanaQuery_TopMatchFullCoverage_NoEvidence_HonestNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "ビートルズ");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var tribute = new Audio { Name = "Bitoruzu Tribute Act", Id = Guid.NewGuid() };
        var filler = new Audio { Name = "Something Else Entirely", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
                q.SearchTerm != null
                    ? new List<BaseItem> { tribute, filler }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the MediaNotFound Tell");
        Assert.True(response.Response.Directives?.All(d => d is not AudioPlayerPlayDirective) != false,
            "the full-coverage tribute pick must never auto-play for a kana-origin query without collision evidence");
    }

    /// <summary>
    /// JF-654 live-battery round 3: the ARTIST fallback arm. The live trace
    /// (deployed a7d42b09): primary 0 for 'bitoruzu' -> ArtistSearch tier-4
    /// InMemoryFuzzyAll plain-fuzzy-matched 'Sator' -> the artist's single item
    /// concat'd to one deduplicated result -> auto-played. JF-652's decision
    /// points never covered SearchMedia, so this arm had NO bar. The resolved
    /// artist now takes the shared JF-471 acceptance predicate (kana leg: user
    /// threshold AND a real DM code collision); the plain-fuzzy Sator match
    /// carries no collision and is the honest MediaNotFound, never a play.
    /// </summary>
    [Fact]
    public async Task HandleAsync_KanaQuery_ArtistFallbackPlainFuzzyMatch_GatedToHonestNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "ビートルズ");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var sator = new MusicArtist { Name = "Sator", Id = Guid.NewGuid() };
        var satorTrack = new Audio { Name = "Sator Song", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { sator };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { satorTrack };
                }

                return new List<BaseItem>();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the MediaNotFound Tell");
        Assert.True(response.Response.Directives?.All(d => d is not AudioPlayerPlayDirective) != false,
            "the plain-fuzzy artist match must never auto-play for a kana-origin query without collision evidence");
    }

    [Fact]
    public async Task HandleAsync_LatinQuery_ArtistFallback_StillAutoPlays()
    {
        // Latin control: the artist arm's acceptance is unchanged for Latin
        // queries (kanaOrigin false, the gate is a no-op), so the single-item
        // artist result still auto-plays (the JF-506-era behavior).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "sator");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var sator = new MusicArtist { Name = "Sator", Id = Guid.NewGuid() };
        var satorTrack = new Audio { Name = "Sator Song", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { sator };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { satorTrack };
                }

                return new List<BaseItem>();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    // --- JF-538: the video-launch announce on the HandleFuzzyMiss auto-play path ---

    /// <summary>
    /// JF-538: a video auto-played through the HandleFuzzyMiss auto-play delegate must
    /// send the launch announce as a progressive response and return a directive-only
    /// final response, the JF-501 contract previously limited to direct launch sites.
    /// Fixture geometry: FuzzyMatchThreshold 95 keeps the site-level FuzzyMatch
    /// pre-check from taking the match ("Matrix" inside "The Matrix" scores exactly
    /// ContainmentScore 90), while FuzzyMatchBehavior.AutoPlay drives HandleFuzzyMiss's
    /// delegate; the same 90 keeps the response unmodified (no closest-match qualifier).
    /// </summary>
    [Fact]
    public async Task HandleAsync_FuzzyMissVideoAutoPlay_AnnounceOn_SendsProgressiveAndDirectiveOnlyResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Matrix");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        user.FuzzyMatchThreshold = 95;
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "The Matrix",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>
            {
                movie,
                new global::MediaBrowser.Controller.Entities.Movies.Movie { Name = "Casablanca", Id = Guid.NewGuid() }
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("The Matrix"), "the fuzzy-miss video auto-play announce must ride the progressive vehicle");
    }

    /// <summary>
    /// JF-538: with the announce toggle off, the fuzzy-miss video auto-play keeps the
    /// silent shape: no progressive announce (only the SearchingMedia ping may arrive,
    /// which never contains the title) and no OutputSpeech on the final response.
    /// </summary>
    [Fact]
    public async Task HandleAsync_FuzzyMissVideoAutoPlay_AnnounceOff_NoProgressiveAnnounceAndNoOutputSpeech()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Matrix");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        user.FuzzyMatchThreshold = 95;
        user.AnnounceNowPlaying = false;
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "The Matrix",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>
            {
                movie,
                new global::MediaBrowser.Controller.Entities.Movies.Movie { Name = "Casablanca", Id = Guid.NewGuid() }
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.False(handler.Progressive.Contains("The Matrix"), "announce off must not send a progressive announce");
    }

    [Fact]
    public async Task HandleAsync_SetsQueueAndNowPlayingItem()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test Song");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audio = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audio.Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(audio, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_DeduplicatesResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audioId = Guid.NewGuid();
        var audio1 = new Audio
        {
            Name = "Test Song",
            Id = audioId
        };
        var audio2 = new Audio
        {
            Name = "Test Song",
            Id = audioId
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Deduplication means single result → plays directly
        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_SearchQueryUsesPlayableTypes()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => { if (++callCount == 1) capturedQuery = q; })
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal("Test", capturedQuery.SearchTerm);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Audio, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Movie, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Episode, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Series, capturedQuery.IncludeItemTypes);
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_ArtistFound_ReturnsArtistSongs()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Soul Coughing");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var song1 = new Audio { Name = "Circles", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() };

        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem>(),           // initial title search: empty
                    2 => new List<BaseItem> { artist },   // artist lookup: found
                    3 => new List<BaseItem> { song1, song2 }, // artist items
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // 2 songs → disambiguation (not auto-play)
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_ArtistFallback_QueryUsesIncludeItemTypesWithoutMediaTypes()
    {
        // JF-667 (JF-358 sibling): the artist-fallback items query is ArtistIds-filtered,
        // so IncludeItemTypes is the single filtering term; MediaTypes must not ride it.
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Soul Coughing");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Circles", Id = Guid.NewGuid() };

        InternalItemsQuery? captured = null;
        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem>(),            // initial title search: empty
                    2 => new List<BaseItem> { artist },   // artist lookup: found
                    3 => new List<BaseItem> { song },     // artist items
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The captured query is the LAST GetItemList call: the artist-items query (call 3),
        // not the title search or the artist lookup before it.
        Assert.NotNull(captured);
        Assert.NotNull(captured!.ArtistIds);
        Assert.Contains(artist.Id, captured.ArtistIds);
        Assert.NotNull(captured.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Audio, captured.IncludeItemTypes);
        // Jellyfin initializes MediaTypes to an empty array: the contract is "no
        // MediaTypes filter", not the field's exact null/empty shape.
        Assert.True(
            captured.MediaTypes == null || captured.MediaTypes.Length == 0,
            "artist-fallback query must not filter via MediaTypes (JF-358/JF-667)");
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_NoArtist_ReturnsMediaNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem>(),  // title search: empty
                    2 => new List<BaseItem>(),  // artist lookup: empty
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SparseResults_ArtistFound_MergesResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Soul Coughing");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var titleResult = new Audio { Name = "Lust in Phaze", Id = Guid.NewGuid() };
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var artistSong = new Audio { Name = "Circles", Id = Guid.NewGuid() };

        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { titleResult },  // 1 title result (sparse)
                    2 => new List<BaseItem> { artist },        // artist found
                    3 => new List<BaseItem> { artistSong },    // artist's songs
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // titleResult + artistSong = 2 items → disambiguation
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_SparseResults_NoArtist_ReturnsOriginalResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent artist");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        // Items whose names won't fuzzy-match the query "nonexistent artist"
        var song1 = new Audio { Name = "Alpha Track", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Beta Track", Id = Guid.NewGuid() };

        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { song1, song2 },  // 2 results (sparse)
                    2 => new List<BaseItem>(),                  // no artist
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Original 2 items, no fuzzy match → disambiguation
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_ManyResults_NoArtistFallback()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var items = Enumerable.Range(0, 5)
            .Select(i => new Audio { Name = $"Song {i}", Id = Guid.NewGuid() })
            .ToList<BaseItem>();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // With >3 results, artist fallback is NOT triggered → only 1 call to GetItemList
        _fx.LibraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once());
    }

    // --- JF-456: out-of-library kinds (playlists) stay searchable for restricted users (GH #22 residual) ---

    private static Entities.User CreateRestrictedUser(Guid libraryId)
    {
        return TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_PlaylistFoundViaUnfilteredSiblingQuery()
    {
        // A library-restricted user searching for a playlist must still find it: the
        // mixed unified query would drop it (TopParentIds excludes the PlaylistsFolder),
        // so the out-of-library sibling query runs WITHOUT the filter (JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "road trip");
        var context = _fx.CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Name = "Road Trip",
            Id = Guid.NewGuid()
        };

        var capturedQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist
                    ? new List<BaseItem> { playlist }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The playlist was found by the sibling query and auto-played as the single result
        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(playlist.Id, Assert.Single(session.NowPlayingQueue).Id);

        // The playlist query carried no library filter; the library-scoped query did
        var playlistQuery = Assert.Single(capturedQueries.Where(q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist));
        Assert.Empty(playlistQuery.TopParentIds);
        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds?.Contains(libraryId) == true);
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_FuzzyMiss_FallsBackToOutOfLibraryKinds()
    {
        // When neither the scoped primary nor the scoped fuzzy pass matches, the
        // out-of-library fuzzy pass still runs (the old mixed fuzzy array would have
        // hidden the playlist behind the TopParentIds filter, JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "road trip playlist");
        var context = _fx.CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Name = "Road Trip Playlist",
            Id = Guid.NewGuid()
        };

        var capturedQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                // Only the FUZZY playlist query matches (SearchTerm null): the primary
                // sibling and every scoped query miss, forcing the fuzzy chain.
                q.IncludeItemTypes.Length == 1
                    && q.IncludeItemTypes[0] == BaseItemKind.Playlist
                    && string.IsNullOrEmpty(q.SearchTerm)
                    ? new List<BaseItem> { playlist }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();

        // The fuzzy out-of-library query ran unfiltered and found the playlist
        var fuzzyPlaylistQuery = Assert.Single(capturedQueries.Where(q =>
            q.IncludeItemTypes.Length == 1
            && q.IncludeItemTypes[0] == BaseItemKind.Playlist
            && string.IsNullOrEmpty(q.SearchTerm)));
        Assert.Empty(fuzzyPlaylistQuery.TopParentIds);
    }

    [Fact]
    public async Task HandleAsync_UnrestrictedUser_SingleUnifiedQuery_IncludesPlaylistKind()
    {
        // Unrestricted users keep the single unified query: the playlist kind rides it
        // with no TopParentIds set, and no sibling query is issued (JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser(); // unrestricted
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var capturedQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Playlist)
            && q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds.Length == 0);
        Assert.DoesNotContain(capturedQueries, q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist);
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_SiblingQuerySkipped_WhenScopedPageSaturatesLimit()
    {
        // Saturation skip (code-review round 2 item 3): a scoped page that came
        // back AT its Limit cannot be improved by the playlist sibling, whose rows
        // the union cap would discard anyway, so the sibling must not be issued:
        // one DB roundtrip saved per attempt on the miss paths inside the 8s window.
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "song");
        var context = _fx.CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        int limit = global::Jellyfin.Plugin.AlexaSkill.Plugin.Instance?.Configuration?.MaxSearchResults ?? 20;
        var fullPage = Enumerable.Range(0, limit)
            .Select(i => new Audio { Name = $"Item {i:000}", Id = Guid.NewGuid() })
            .ToList<BaseItem>();

        var capturedQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Contains(BaseItemKind.Playlist)
                    ? new List<BaseItem>() // sibling must never be reached
                    : fullPage);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The scoped query ran, carried the restriction, and returned a full page.
        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds?.Contains(libraryId) == true);
        // No playlist-only sibling query was issued (no primary one, and none from
        // the fuzzy chain either: results were non-empty so the chain never ran).
        Assert.DoesNotContain(capturedQueries, q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist);
    }

    // --- JF-506: song-title retry on the confirmed not-found path ---
    // Live evidence corr=3240220d: a song title that reached this handler through a
    // generic carrier dead-ended in MediaNotFound because the fuzzy pass scans only
    // the first 500 rows. The n-gram index is the complete O(1) song-title lookup.

    [Fact]
    public async Task HandleAsync_ZeroResults_SongIndexHit_PlaysTheSong()
    {
        var songIndex = new Mock<ISongNgramIndex>();
        var handler = CreateHandlerWithSongIndex(songIndex);
        var request = CreateIntentRequest(query: "screenwriters blues");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var song = new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() };
        songIndex.Setup(i => i.Search(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()))
            .Returns(new List<(BaseItem, double)> { (song, 100.0) });

        // Every DB pass misses: title search, artist fallback, and the fuzzy scan.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, Assert.Single(session.NowPlayingQueue).Id);
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_SongIndexMiss_StillReturnsMediaNotFound()
    {
        var songIndex = new Mock<ISongNgramIndex>();
        var handler = CreateHandlerWithSongIndex(songIndex);
        var request = CreateIntentRequest(query: "nonexistent");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        songIndex.Setup(i => i.Search(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()))
            .Returns(new List<(BaseItem, double)>());
        songIndex.Setup(i => i.SearchPhonetic(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()))
            .Returns(new List<(BaseItem, double)>());

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_SongIndexWarming_DegradesToNotFound()
    {
        // The retry is an opportunistic fallback, not a gated title-only path: a
        // warming song index must degrade to the clean not-found, never surface as
        // a warming refusal or a crash on a unified-content search.
        var songIndex = new Mock<ISongNgramIndex>();
        var handler = CreateHandlerWithSongIndex(songIndex);
        var request = CreateIntentRequest(query: "screenwriters blues");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        songIndex.Setup(i => i.Search(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()))
            .Throws(new SkillWarmingUpException("song n-gram"));

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_MusicDisabled_SkipsSongIndexRetry()
    {
        // JF-466 hard-zero contract: a music-disabled user must never see song
        // results, so the index is not even consulted. FilterByContentAccess reads
        // Plugin.Instance.Configuration, so the live instance must exist here
        // (HandlerTestFixture does not create it and collection class order is
        // nondeterministic).
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            _ => { },
            "jf506-searchmedia-retry");
        var songIndex = new Mock<ISongNgramIndex>();
        var handler = CreateHandlerWithSongIndex(songIndex);
        var request = CreateIntentRequest(query: "screenwriters blues");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var liveConfig = global::Jellyfin.Plugin.AlexaSkill.Plugin.Instance!.Configuration;
        bool originalMusic = liveConfig.MusicEnabled;
        liveConfig.MusicEnabled = false;

        SkillResponse response;
        try
        {
            response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);
        }
        finally
        {
            liveConfig.MusicEnabled = originalMusic;
        }

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
        songIndex.Verify(
            i => i.Search(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()),
            Times.Never);
    }
}
