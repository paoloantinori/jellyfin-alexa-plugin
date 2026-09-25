using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class SleepTimerIntentHandlerTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;
    private readonly IDisposable _pluginQueueSwap;

    public SleepTimerIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });

        // The ledger/launch-scope writes go through Plugin.Instance's manager (the
        // JF-522/JF-628 block has no injected queue manager); the swap scope below
        // points the plugin at this suite's manager.
        _queueManager = TestHelpers.CreateDeviceQueueManager("sleep-timer-tests");
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => { },
            "sleep-timer-tests");

        // AFTER EnsurePluginInstance: the helper's create path overwrites
        // ServerAddress on the SAME config reference (its mocked deserializer
        // returns this instance), so a pin placed before it is silently dead.
        TestHelpers.SetServerAddress(_config, "https://test.example.com");

        _pluginQueueSwap = TestHelpers.SwapPluginQueueManager(_queueManager);
    }

    public void Dispose()
    {
        _pluginQueueSwap.Dispose();
        GC.SuppressFinalize(this);
    }

    private SleepTimerIntentHandler CreateHandler()
    {
        return new SleepTimerIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            _libraryManagerMock.Object,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? durationValue = null)
    {
        var intent = new Intent { Name = IntentNames.SleepTimer };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (durationValue != null)
        {
            intent.Slots["sleep_duration"] = new global::Alexa.NET.Request.Slot { Name = "sleep_duration", Value = durationValue };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    [Fact]
    public void CanHandle_SleepTimerIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "30");

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
    public async Task HandleAsync_MissingDuration_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "30");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_SetsSleepTimer_ReturnsTimerConfirmation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "30");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    // JF-618: the duration slot carries its own unit (AMAZON.DURATION, ISO 8601).
    // The live incident: "ferma dopo 5 secondi" with a number-typed minutes slot set
    // a 5-MINUTE timer; seconds must now be seconds.

    [Theory]
    [InlineData("PT30S", 30)]          // thirty seconds
    [InlineData("PT5M", 300)]          // five minutes
    [InlineData("PT1H30M", 5400)]      // an hour and a half
    [InlineData("PT0M30S", 30)]        // zero minutes thirty seconds
    [InlineData("P1W", 604800)]        // week form: valid ISO 8601, rejected by XSD (live probe)
    [InlineData("30", 1800)]           // bare number: minutes (raw-passthrough shape)
    [InlineData("P100000000D", null)]  // overflow: elicits, does not escape the handler
    [InlineData("99999999999999999999", null)] // absurd bare number: elicits
    public void ParseSleepDuration_CarriesTheSpokenUnit(string raw, int? expectedSeconds)
    {
        Assert.Equal(expectedSeconds.HasValue ? TimeSpan.FromSeconds(expectedSeconds.Value) : null,
            Jellyfin.Plugin.AlexaSkill.Alexa.Util.ResumeMath.ParseAlexaDuration(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("un po'")]
    public void ParseSleepDuration_Unparseable_IsNull(string? raw)
    {
        Assert.Null(Jellyfin.Plugin.AlexaSkill.Alexa.Util.ResumeMath.ParseAlexaDuration(raw));
    }

    [Fact]
    public async Task HandleAsync_SecondsDuration_SpeaksSecondsAndShortDeadline()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "PT30S");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response!.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        // The sleep deadline rides the stream token: 30s out, not 30 minutes out.
        string token = directive.AudioItem.Stream.Token;
        int sep = token.IndexOf("|sleep:", StringComparison.Ordinal);
        Assert.True(sep > 0, $"expected a sleep token, got {token}");
        long deadlineTicks = long.Parse(token[(sep + 7)..], System.Globalization.CultureInfo.InvariantCulture);
        double minutesOut = (new DateTimeOffset(deadlineTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.InRange(minutesOut, 0, 1);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("30 seconds", speech.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_SingleMinute_SpeaksSingular()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "PT1M");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Contains("one minute", speech.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1 minutes", speech.Text, StringComparison.Ordinal);
    }

    // JF-618 platform truth (live log corr f560baa9): the deadline is enforced only
    // at track boundaries; mid-track there are no events and no proactive Stop
    // channel. When the deadline lands inside the current track, the confirmation
    // says so instead of promising a mid-song stop.


    [Fact]
    public async Task HandleAsync_DeadlineInsideTrack_SpeaksTrackEndTruth()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "PT30S");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // A 7-minute track 2 minutes in: 30 seconds cannot stop it mid-song.
        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(7).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Contains("30 seconds", speech.Text, StringComparison.Ordinal);
        Assert.Contains("end of the current track", speech.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DeadlineBeyondTrack_SpeaksPlainSet()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "PT2H");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(7).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response!.OutputSpeech);
        Assert.Contains("2 hours", speech.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("end of the current track", speech.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_ZeroDuration_CancelsTimer()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "0");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_ZeroDuration_MidSleepCompositeToken_ReplaysWithCleanIdAndNoDeadline()
    {
        // JF-447 review finding (cancel branch): during sleep playback the AudioPlayer
        // token carries the sleep suffix. The cancel replay must be built from the CLEAN
        // id: a composite id in the stream URL path is unmatchable, and a composite
        // replay Token would carry the old deadline so the sleep would still fire after
        // the cancel.
        Guid songId = Guid.NewGuid();
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "0");
        var context = CreateContext();
        context.AudioPlayer = new PlaybackState
        {
            Token = $"{songId}|sleep:{DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks}",
            OffsetInMilliseconds = 90_000
        };
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = songId };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(1).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var directive = Assert.Single(response.Response!.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Equal(songId.ToString(), directive.AudioItem.Stream.Token);
        Assert.DoesNotContain("|sleep:", directive.AudioItem.Stream.Token, StringComparison.Ordinal);
        Assert.Contains(songId.ToString(), directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("|sleep:", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(90_000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    // JF-628: the re-issue directive is a ReplaceAll AudioPlayer.Play minted OUTSIDE
    // the BuildAudioPlayerResponse chokepoint (its own comment says so), so it owes
    // the chokepoint's ledger write itself (RecordLastPlayed's invariant: every launch
    // site records). Arming mid-album must move the device ledger onto the ARMED item;
    // pre-JF-628 it stayed pinned on the older launch track the chokepoint recorded
    // when the album started, desyncing from the composite token.

    /// <summary>
    /// Shared JF-628 arrange: pre-pins the device ledger on an EARLIER launch's record,
    /// runs the sleep handler over the re-issued track's token/session, and returns the
    /// response plus the ledger snapshot the handler left behind. The arm and cancel
    /// branches differ only in duration and token shape; the VideoApp branches differ
    /// in the seed route and (JF-632) a library-resolvable seed item, which is what
    /// lets the medium gate classify. Directive shape is asserted by the callers: the
    /// audio routes mint the re-issue, the VideoApp routes refuse with none.
    /// </summary>
    private async Task<(SkillResponse Response, string? ItemId, DeviceQueueManager.LaunchRoute? Route)> ReissueAndReadLedgerAsync(
        string durationValue, string reissuedTrackToken, Guid reissuedTrackId,
        DeviceQueueManager.LaunchRoute seedRoute = DeviceQueueManager.LaunchRoute.Audio,
        BaseItem? seedLedgerItem = null)
    {
        Guid earlierLaunchId = seedLedgerItem?.Id ?? Guid.NewGuid();
        if (seedLedgerItem != null)
        {
            _libraryManagerMock.Setup(x => x.GetItemById(seedLedgerItem.Id)).Returns(seedLedgerItem);
        }

        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: durationValue);
        var context = CreateContext();
        context.AudioPlayer = new PlaybackState
        {
            // The queue advanced past the earlier launch, so the token (and the
            // session's now-playing item) name the track being re-issued.
            Token = reissuedTrackToken,
            OffsetInMilliseconds = 90_000
        };
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Reissued Track", Id = reissuedTrackId, RunTimeTicks = TimeSpan.FromMinutes(7).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(1).Ticks };

        _queueManager.RecordLastPlayed(DeviceId, earlierLaunchId.ToString(), seedRoute);
        Assert.Equal(earlierLaunchId.ToString(), _queueManager.GetLastPlayedItemId(DeviceId));

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        (string? itemId, DeviceQueueManager.LaunchRoute? route) = _queueManager.GetLastPlayedSnapshot(DeviceId);
        return (response, itemId, route);
    }

    [Fact]
    public async Task HandleAsync_ArmingMidAlbum_RecordsLedgerOnArmedItem()
    {
        Guid armedTrackId = Guid.NewGuid();

        (SkillResponse response, string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", armedTrackId.ToString(), armedTrackId);

        // The audio-routed re-issue still mints the directive.
        Assert.NotEmpty(response.Response!.Directives);

        // The ledger names the ARMED item on the audio route, agreeing with the
        // composite token instead of the older launch track.
        Assert.Equal(armedTrackId.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.Audio, route);
    }

    [Fact]
    public async Task HandleAsync_CancelReplayMidSleepComposite_RecordsLedgerOnReplayedItem()
    {
        // The cancel replay re-issues the item too (same ReplaceAll directive, clean
        // token), so the shared write site must cover this branch as well.
        Guid armedTrackId = Guid.NewGuid();
        string compositeToken = $"{armedTrackId}|sleep:{DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks}";

        (SkillResponse response, string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("0", compositeToken, armedTrackId);

        Assert.NotEmpty(response.Response!.Directives);
        Assert.Equal(armedTrackId.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.Audio, route);
    }

    [Fact]
    public async Task HandleAsync_ArmingOverVideoAppRoutedLedger_KeepsTheVideoAppRecord()
    {
        // Review finding on JF-628, re-pinned by JF-632: a VideoApp launch on screen
        // never touches context.AudioPlayer.Token, so a sleep arm there resolves the
        // STALE audio token/session. JF-632 gates the whole re-issue on the medium:
        // over the resolvable VideoApp-routed movie the arm answers the honest
        // refusal Tell and mints NO AudioPlayer.Play (the old shape re-issued the
        // stale audio item over the running video: parallel audio the platform
        // cannot stop), and the truthful (movie, VideoApp) ledger record survives:
        // no event ever re-writes the ledger, so an overwrite would poison the
        // medium readers (pause/next refusals, resume arbitration) persistently.
        Guid staleSongId = Guid.NewGuid();
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "New Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/new.mkv"
        };

        (SkillResponse response, string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", staleSongId.ToString(), staleSongId,
                seedRoute: DeviceQueueManager.LaunchRoute.VideoApp,
                seedLedgerItem: movie);

        // The honest refusal: speech, session end, NO re-issue directive.
        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response?.ShouldEndSession);
        Assert.Contains("sleep timer", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sleep timer set", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);

        // The seeded VideoApp entry survives the arm untouched.
        Assert.Equal(movie.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }

    [Fact]
    public async Task HandleAsync_ArmingOverVideoAppBook_RefusesInsteadOfParallelAudioPlay()
    {
        // JF-632, the book arm of the refusal family: a NativeControlsForBooks book
        // rides the VideoApp HLS path, which never touches the AudioPlayer token and
        // emits no events the deadline could ride, so the arm refuses with the same
        // video-family line (the PauseIntentHandler JF-564 precedent gives books no
        // line of their own) and the ledger keeps the truthful book record.
        Guid staleSongId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Test Book",
            Id = Guid.NewGuid(),
            Path = "/books/test.m4b"
        };

        (SkillResponse response, string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", staleSongId.ToString(), staleSongId,
                seedRoute: DeviceQueueManager.LaunchRoute.VideoApp,
                seedLedgerItem: book);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response?.ShouldEndSession);
        Assert.Contains("sleep timer", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(book.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }

    [Fact]
    public async Task HandleAsync_ArmingOverSeekModeMusic_RefusesInsteadOfDoubleAudio()
    {
        // JF-632, the judgment the task asked for: the JF-625 seek-mode music IS
        // audio, and a sleep timer over it is a legitimate wish, but the medium gate
        // must refuse anyway. Two independent reasons: the re-issue directive is an
        // AudioPlayer.Play ReplaceAll of the STALE pre-launch token (the seek-mode
        // launch never touched it), which doubles the audio of the very track
        // playing with no VideoApp.Stop to clean it up; and even a correct re-issue
        // could never honor the deadline, because VideoApp playback emits no events
        // and the sleep deadline is enforced only at PlaybackNearlyFinished on the
        // AudioPlayer path. The refusal speaks the seek-mode line, not the
        // music-less video line, because the medium IS music.
        Guid preLaunchSongId = Guid.NewGuid();
        var seekModeTrack = new Audio
        {
            Name = "Seek Mode Track",
            Id = Guid.NewGuid(),
            Path = "/music/seek.flac"
        };

        (SkillResponse response, string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", preLaunchSongId.ToString(), seekModeTrack.Id,
                seedRoute: DeviceQueueManager.LaunchRoute.VideoApp,
                seedLedgerItem: seekModeTrack);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response?.ShouldEndSession);
        // The seek-mode line, not the video-family line.
        Assert.Contains("progress bar", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live TV", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);

        Assert.Equal(seekModeTrack.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }
}
