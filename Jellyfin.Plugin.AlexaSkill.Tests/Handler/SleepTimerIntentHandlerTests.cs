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
using MediaBrowser.Controller.Entities.Audio;
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
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;
    private readonly DeviceQueueManager? _previousPluginQueueManager;

    public SleepTimerIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });

        // The ledger/launch-scope writes go through Plugin.Instance's manager (the
        // handler has no injected queue manager); point the plugin at this suite's
        // manager and restore the previous value on dispose (the temp dir is owned
        // by the registered sweep).
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

        _previousPluginQueueManager = Jellyfin.Plugin.AlexaSkill.Plugin.Instance?.DeviceQueueManager;
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _queueManager;
        }
    }

    public void Dispose()
    {
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _previousPluginQueueManager;
        }

        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private SleepTimerIntentHandler CreateHandler()
    {
        return new SleepTimerIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
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
    /// ledger snapshot the handler left behind. The arm and cancel branches differ only
    /// in duration and token shape; the VideoApp branch differs only in the seed route.
    /// </summary>
    private async Task<(string? ItemId, DeviceQueueManager.LaunchRoute? Route)> ReissueAndReadLedgerAsync(
        string durationValue, string reissuedTrackToken, Guid reissuedTrackId,
        DeviceQueueManager.LaunchRoute seedRoute = DeviceQueueManager.LaunchRoute.Audio)
    {
        Guid earlierLaunchId = Guid.NewGuid();
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
        Assert.NotEmpty(response.Response!.Directives);
        return _queueManager.GetLastPlayedSnapshot(DeviceId);
    }

    [Fact]
    public async Task HandleAsync_ArmingMidAlbum_RecordsLedgerOnArmedItem()
    {
        Guid armedTrackId = Guid.NewGuid();

        (string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", armedTrackId.ToString(), armedTrackId);

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

        (string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("0", compositeToken, armedTrackId);

        Assert.Equal(armedTrackId.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.Audio, route);
    }

    [Fact]
    public async Task HandleAsync_ArmingOverVideoAppRoutedLedger_KeepsTheVideoAppRecord()
    {
        // Review finding on JF-628: a VideoApp launch on screen never touches
        // context.AudioPlayer.Token, so a sleep arm there resolves the STALE audio
        // token/session. The ledger write must not overwrite the truthful
        // (video item, VideoApp) record with (stale audio item, Audio): no event
        // ever re-writes the ledger, so the poison would persist and break the
        // medium readers (pause/next refusals, resume arbitration).
        Guid staleSongId = Guid.NewGuid();

        (string? itemId, DeviceQueueManager.LaunchRoute? route) =
            await ReissueAndReadLedgerAsync("PT30S", staleSongId.ToString(), staleSongId,
                seedRoute: DeviceQueueManager.LaunchRoute.VideoApp);

        // The seeded VideoApp entry survives the arm untouched.
        Assert.NotEqual(staleSongId.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }
}
