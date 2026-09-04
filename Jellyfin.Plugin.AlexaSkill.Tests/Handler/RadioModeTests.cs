using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class RadioModeTests : PluginTestBase, IDisposable
{
    private static readonly string DeviceId = "test-device";
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILiveTvStreamResolver> _resolverMock;

    public RadioModeTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        // By default the resolver returns a direct-remote stream so channel-tier tests
        // reach the VideoApp.Launch path (same default as PlayChannelIntentHandlerTests).
        _resolverMock = new Mock<ILiveTvStreamResolver>();
        _resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTvStream("https://remote.example/radio.m3u8"));

        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
    }

    public void Dispose()
    {
        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
        GC.SuppressFinalize(this);
    }

    private PlayRadioIntentHandler CreateRadioHandler()
        => new(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _resolverMock.Object, _loggerFactory);

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string token)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token,
            OffsetInMilliseconds = 0
        };
    }

    [Fact]
    public void PlayRadio_CanHandle_ReturnsTrue()
    {
        var handler = CreateRadioHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void TurnRadioOn_CanHandle_ReturnsTrue()
    {
        var handler = new TurnRadioOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void TurnRadioOff_CanHandle_ReturnsTrue()
    {
        var handler = new TurnRadioOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    private static IntentRequest CreatePlayRadioRequest(string? stationValue = null, bool dialogInProgress = false, string locale = "en-US")
    {
        var request = new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" }, Locale = locale };
        if (stationValue != null)
        {
            request.Intent.Slots = new Dictionary<string, Slot>
            {
                ["station"] = new Slot { Name = "station", Value = stationValue }
            };
        }

        if (dialogInProgress)
        {
            request.DialogState = "IN_PROGRESS";
        }

        return request;
    }

    /// <summary>
    /// Real device request shapes for context.AudioPlayer (JF-480). Live corr=2c2d8676:
    /// after a pause every customer-initiated request carries playerActivity STOPPED
    /// with the paused item's token and the pause offset; during active playback the
    /// state is PLAYING (Amazon docs: playerActivity is the last known playback state).
    /// </summary>
    private static Context CreateContextWithPlayerActivity(string playerActivity, string token, long offsetMs)
    {
        var context = CreateContext();
        context.AudioPlayer = new PlaybackState
        {
            Token = token,
            OffsetInMilliseconds = offsetMs,
            PlayerActivity = playerActivity
        };
        return context;
    }

    /// <summary>
    /// JF-472: bare forms ("suona jazz") stolen by Amazon's NLU arrive as PlayRadioIntent
    /// with an empty station slot while nothing plays. The handler must elicit the
    /// station (session open) instead of answering the out-of-context nothing-playing Tell.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_NothingPlaying_ElicitsStation()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession == true, "the station elicit must keep the session open");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Anti-pattern #7: whitespace-only slot values count as empty.</summary>
    [Fact]
    public async Task PlayRadio_WhitespaceStationSlot_NothingPlaying_ElicitsStation()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("  "),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    /// <summary>
    /// JF-480 (live evidence corr=2c2d8676): after a PAUSE the requester's Jellyfin
    /// session can still hold the play path's optimistic FullNowPlayingItem. The
    /// plugin's pause emits AudioPlayer.Stop, the platform answers PlaybackStopped, and
    /// Jellyfin's OnPlaybackStopped clears the now-playing item only on the session of
    /// the device that SENT the event; in a multi-room group that is the member device,
    /// not the coordinator the voice request came from. The real post-pause request
    /// shape is context.AudioPlayer with playerActivity STOPPED, the paused item's
    /// token, and the pause offset. Item presence alone must NOT seed radio mode: the
    /// paused device gets the station elicit exactly like an idle one.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_PausedWithSurvivingItem_ElicitsStation()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Delicate" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        var context = CreateContextWithPlayerActivity("STOPPED", currentId.ToString(), 16_384);

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession == true, "the station elicit must keep the session open");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
        Assert.False(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID), "radio mode must not start from a paused track");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Amazon's playerActivity PAUSED ("stream was paused", per the request/response
    /// JSON reference) is the same not-actively-playing bucket as STOPPED: no radio
    /// seeding from a paused stream.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_PlayerActivityPaused_ElicitsStation()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Delicate" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContextWithPlayerActivity("PAUSED", currentId.ToString(), 16_384),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
    }

    /// <summary>
    /// The elicit is conditional on nothing actively playing: with a current track
    /// actively playing (playerActivity PLAYING, the state intent requests carry during
    /// playback) the context seeds the radio, so every slot-less sample ("riproduci
    /// radio") must keep starting radio mode directly (JF-472 acceptance: actively
    /// playing = unchanged behavior, JF-480 narrowed "playing" to this state).
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_SomethingPlaying_StartsRadioMode()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        var context = CreateContextWithPlayerActivity("PLAYING", currentId.ToString(), 42_000);

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" } });
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID), "radio mode must still start from the current track");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    /// <summary>
    /// BUFFER_UNDERRUN is a transient mid-playback state (same reading as
    /// PlaybackFinishedEventHandler.hasQueuedNext): the stream is still the active
    /// playback, so the radio seed keeps working.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_BufferUnderrun_StartsRadioMode()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        var context = CreateContextWithPlayerActivity("BUFFER_UNDERRUN", currentId.ToString(), 42_000);

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" } });
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID), "a buffering stream is still the active playback");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
    }

    /// <summary>
    /// JF-474 tier (i): a station word matching a live-TV RADIO channel plays that
    /// channel through the PlayChannel machinery (VideoApp.Launch, resolver URL,
    /// ShouldEndSession null), never the nothing-playing Tell.
    /// </summary>
    [Fact]
    public async Task PlayRadio_StationGiven_ChannelMatch_PlaysChannel()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();

        var channel = new Movie { Name = "Jazz FM", Id = Guid.NewGuid() };
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.SearchTerm == "jazz fm")))
            .Returns(new List<BaseItem> { channel });

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("jazz fm"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Null(response.Response.ShouldEndSession);
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "VideoApp.Launch");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(channel.Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(channel.Id, Assert.Single(session.NowPlayingQueue!).Id);
        Assert.False(RadioModeState.IsEnabled(session.UserId, DeviceId), "a channel launch is not radio mode");
        _resolverMock.Verify(r => r.ResolveAsync(channel, It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()), Times.Once);
        var text = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 tier (i) hard zero: when the resolver cannot produce a stream for the
    /// matched channel, the response is the same not-available Tell PlayChannel speaks.
    /// </summary>
    [Fact]
    public async Task PlayRadio_StationGiven_ChannelMatch_UnresolvableStream_SpeaksNotAvailable()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LiveTvStream?)null);

        var channel = new Movie { Name = "Jazz FM", Id = Guid.NewGuid() };
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.SearchTerm == "jazz fm")))
            .Returns(new List<BaseItem> { channel });

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("jazz fm"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession == true, "the not-available Tell ends the session");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "VideoApp.Launch");
    }

    /// <summary>
    /// JF-474 tier (ii): a genre word with no channel match seeds radio mode with that
    /// genre's tracks (FindRadioTracksByGenreAsync), announces RadioStarted, and enables
    /// RadioModeState so PlaybackNearlyFinished continues the queue. This is the
    /// end-to-end healing of the JF-472 Amazon-side misroute: "suona jazz" -> elicit ->
    /// "jazz" answer -> genre radio plays.
    /// </summary>
    [Fact]
    public async Task PlayRadio_StationGiven_GenreWord_StartsGenreRadio()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();

        var genreTracks = new List<BaseItem>
        {
            new Audio { Id = Guid.NewGuid(), Name = "Blue in Green" },
            new Audio { Id = Guid.NewGuid(), Name = "So What" },
        };
        SetupGenreQuery(genreTracks);

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("jazz"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
        Assert.True(RadioModeState.IsEnabled(session.UserId, DeviceId), "the genre tier must enable radio mode");
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(2, session.NowPlayingQueue!.Count);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 tier (iii): a word that is neither a channel nor a genre gets the
    /// TRUTHFUL not-found naming the station word and suggesting a genre, never the
    /// out-of-context nothing-playing Tell (the JF-474 dead-end).
    /// </summary>
    [Fact]
    public async Task PlayRadio_StationGiven_NoChannelNoGenre_TruthfulNotFound()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("xyzzyfoo"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession == true, "the not-found Tell ends the session");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("xyzzyfoo", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jazz", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 UX a: the elicit's REPROMPT names 2-3 real options from the live-TV radio
    /// channel list, while the FIRST ask stays the short RadioAskStation question.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_RepromptNamesChannelOptions()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        SetupRadioChannelList("Jazz FM", "RTL 102.5", "Radio Italia");

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        Assert.NotNull(response.Response.Reprompt?.OutputSpeech);
        string ask = TestHelpers.GetSpeechText(response);
        Assert.Contains("station", ask, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jazz FM", ask, StringComparison.OrdinalIgnoreCase);

        string reprompt = RepromptText(response);
        Assert.Contains("Jazz FM", reprompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RTL 102.5", reprompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 UX a fallback: with no live-TV radio channels in the library the reprompt
    /// suggests the genre words instead, and the elicit still fires.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_NoChannels_GenreWordReprompt()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        string reprompt = RepromptText(response);
        Assert.Contains("jazz", reprompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rock", reprompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 UX b: during the open elicit a question-shaped answer ("what are the
    /// options?") gets the available-list response plus a RE-ASK (still an elicit, so
    /// the follow-up keeps filling the station slot), not the dead-end Tell.
    /// </summary>
    [Fact]
    public async Task PlayRadio_QuestionShapedAnswer_ListsChannelsAndReAsks()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        SetupRadioChannelList("Jazz FM", "RTL 102.5");

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("what are the options", dialogInProgress: true),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession == true, "the help answer must keep the session open");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jazz FM", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(RadioModeState.IsEnabled(session.UserId, DeviceId), "a help question must not start playback");
    }

    /// <summary>
    /// JF-474 UX b, it-IT vocabulary: "quali ci sono" is a question in the Italian
    /// locale's help-word set, so the it-IT device case (the JF-474 device evidence)
    /// lists options and re-asks.
    /// </summary>
    [Fact]
    public async Task PlayRadio_QuestionShapedAnswer_Italian_QualiCiSono_ListsAndReAsks()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        SetupRadioChannelList("Jazz FM");

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("quali ci sono", dialogInProgress: true, locale: "it-IT"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jazz FM", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("genere", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 UX b, no-channels help shape: the available-list answer falls back to the
    /// genre suggestion and still re-asks via the elicit.
    /// </summary>
    [Fact]
    public async Task PlayRadio_QuestionShapedAnswer_NoChannels_ListsGenresAndReAsks()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("list", dialogInProgress: true),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("jazz", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-474 review P3-2: the apostrophe is a separator so contractions tokenize
    /// ("what's on" -> "what" + "on"); a future "simplification" of the separator
    /// list would silently break this. Pinned at the predicate level.
    /// </summary>
    [Fact]
    public void QuestionWords_ContractionAndTokenDetection_Pinned()
    {
        Assert.True(global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.QuestionWords.IsQuestion("what's on", "en-US"));
        Assert.True(global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.QuestionWords.IsQuestion("what is on", "en-US"));
        Assert.True(global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.QuestionWords.IsQuestion("quali ci sono", "it-IT"));
        Assert.False(global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.QuestionWords.IsQuestion("whatsername", "en-US"));
        Assert.False(global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.QuestionWords.IsQuestion("jazz fm", "en-US"));
    }

    /// <summary>
    /// The question detection is token-based: a station word that merely CONTAINS a
    /// help word without a token boundary ("whatsername") is not a question and falls
    /// through the tiers to the truthful not-found.
    /// </summary>
    [Fact]
    public async Task PlayRadio_QuestionWordEmbeddedInName_IsNotAQuestion()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("whatsername", dialogInProgress: true),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Falls through the tiers to the truthful not-found (names the word), NOT the
        // help listing (which would contain the genre suggestion without the word).
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("whatsername", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    /// <summary>
    /// The enrichment query is fail-soft by design: when the channel-name lookup throws
    /// (transient DB error), the elicit still fires with the genre-word reprompt instead
    /// of failing the whole question.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_ChannelListQueryThrows_ElicitStillFires()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        SetupJellyfinUser();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.SearchTerm == null)))
            .Throws(new InvalidOperationException("db hiccup"));

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        Assert.Contains("jazz", RepromptText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reprompt's plain-text speech (the elicit reprompts are plain text; IOutputSpeech.ToString() is just the type name).</summary>
    private static string RepromptText(SkillResponse response)
        => (response.Response.Reprompt?.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;

    private void SetupJellyfinUser()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    /// <summary>Genre queries (Genres filter set) return the given tracks; every other query finds nothing.</summary>
    private void SetupGenreQuery(List<BaseItem> genreTracks)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.Genres != null && q.Genres.Count > 0)))
            .Returns(genreTracks);
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.Genres == null || q.Genres.Count == 0)))
            .Returns(new List<BaseItem>());
    }

    /// <summary>The elicit reprompt's channel-name listing query: LiveTvChannel rows with no SearchTerm.</summary>
    private void SetupRadioChannelList(params string[] names)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(
                q => q.SearchTerm == null && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel))))
            .Returns(names.Select(n => (BaseItem)new Movie { Name = n, Id = Guid.NewGuid() }).ToList());
    }

    /// <summary>
    /// Escape hatch from the elicitation trap (JF-423 pattern): while the station elicit
    /// is open (dialog IN_PROGRESS), a bare cancel word is captured into the slot instead
    /// of routing to AMAZON.Stop/CancelIntent; it must end the flow, not be answered as
    /// a station.
    /// </summary>
    [Fact]
    public async Task PlayRadio_CapturedCancelWordDuringOpenElicit_EndsFlow()
    {
        var handler = CreateRadioHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("stop", dialogInProgress: true),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("stopped", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true, "the cancel escape must end the session");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    [Fact]
    public async Task TurnRadioOn_EnablesRadioMode()
    {
        var handler = new TurnRadioOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } },
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOff_DisablesRadioMode()
    {
        var handler = new TurnRadioOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);
        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } },
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_WithRadioMode_AutoQueuesSimilar()
    {
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };

        session.FullNowPlayingItem = currentAudio;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        var similarId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { new Audio { Id = similarId, Name = "Similar Rock Song" } });

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(session.NowPlayingQueue.Count > 1);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_WithoutRadioMode_ReturnsEmpty()
    {
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = currentId, Name = "Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
    }
}
