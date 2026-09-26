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
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-636 facts for the SetPlaybackSpeed handler: the slot elicit, the
/// no-media Tell, the mid-play re-launch position carry (stream offset scaled
/// by the launch scope's rate, base composed, converted back into the new
/// stream's ?start=), rate cycling from the ACTIVE scope rate, the standing
/// preference write, and the JF-632 VideoApp-medium honest refusal.
/// </summary>
[Collection("Plugin")]
public class SetPlaybackSpeedIntentHandlerTests : PluginTestBase
{
    private const string DeviceId = "speed-tests-device";

    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly DeviceQueueManager _queueManager;

    public SetPlaybackSpeedIntentHandlerTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _queueManager = TestHelpers.CreateDeviceQueueManager(
            "setplaybackspeed", _loggerFactory.CreateLogger<DeviceQueueManager>());
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private SetPlaybackSpeedIntentHandler CreateHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            _libraryManagerMock.Object,
            _queueManager);

    private static IntentRequest CreateIntentRequest(Slot? speedSlot = null, string locale = "it-IT")
    {
        var intent = new Intent { Name = IntentNames.SetPlaybackSpeed };
        intent.Slots = new Dictionary<string, Slot>();
        if (speedSlot != null)
        {
            intent.Slots[IntentNames.Slots.Speed] = speedSlot;
        }

        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req", DialogState = "COMPLETED" };
    }

    private static Slot RateSlot(string value, string? id = null)
    {
        var slot = new Slot { Name = IntentNames.Slots.Speed, Value = value };
        if (id == null)
        {
            return slot;
        }

        slot.Resolution = new global::Alexa.NET.Request.Resolution
        {
            Authorities = new[]
            {
                new global::Alexa.NET.Request.ResolutionAuthority
                {
                    Status = new global::Alexa.NET.Request.ResolutionStatus { Code = "ER_SUCCESS_MATCH" },
                    Values = new[]
                    {
                        new global::Alexa.NET.Request.ResolutionValueContainer
                        {
                            Value = new global::Alexa.NET.Request.ResolutionValue { Name = value, Id = id }
                        }
                    }
                }
            }
        };
        return slot;
    }

    private Context CreatePlayingContext(BaseItem item, long offsetMs)
    {
        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState
        {
            Token = item.Id.ToString(),
            OffsetInMilliseconds = offsetMs,
            PlayerActivity = "PLAYING"
        };
        return context;
    }

    private SessionInfo CreateSession(BaseItem? nowPlaying)
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.FullNowPlayingItem = nowPlaying;
        return session;
    }

    private static Audio CreateEpisode(int runtimeMinutes)
        => new()
        {
            Name = "Podcast episode",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(runtimeMinutes).Ticks
        };

    private void SetupItemLookup(BaseItem item)
        => _libraryManagerMock.Setup(m => m.GetItemById(item.Id)).Returns(item);

    // ---- slot handling ----

    [Fact]
    public async Task MissingSlot_AsksWithMicOpen_NeverDeadMicTell()
    {
        var handler = CreateHandler();
        SessionInfo session = CreateSession(null);

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(), TestHelpers.CreateTestContext(DeviceId), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot"));
        Assert.Equal(false, response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task UnrecognizedSpeedValue_AlsoAsks()
    {
        var handler = CreateHandler();
        SessionInfo session = CreateSession(null);

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("tartaruga")), TestHelpers.CreateTestContext(DeviceId), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot"));
    }

    [Fact]
    public async Task NoResolvableItem_TellsNoMediaPlaying()
    {
        var handler = CreateHandler();
        SessionInfo session = CreateSession(null);

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("più veloce", PlaybackSpeed.FasterId)),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("Nessun contenuto in riproduzione", SpeechText(response), StringComparison.Ordinal);
    }

    // ---- the mid-play re-launch position carry ----

    [Fact]
    public async Task DirectRate_OnRawStaticStream_SeeksTheRawContentPosition()
    {
        // Rate 1000 scope (base 0): the raw device offset IS the content position.
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(DeviceId, episode.Id.ToString(), 0, enqueued: false, ratePerMille: 1000);

        var handler = CreateHandler();
        Context context = CreatePlayingContext(episode, offsetMs: 10 * 60 * 1000); // 10 content minutes
        var user = TestHelpers.CreateTestUser();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("uno e mezzo", "1500")), context, user, CreateSession(episode), CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/1500/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        // Content position 10min is the INPUT seek (ticks), and the directive
        // offset is 0 (the output timeline starts at the seek point).
        Assert.Contains($"start={TimeSpan.FromMinutes(10).Ticks}", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(1500, user.PodcastSpeedPerMille);
    }

    [Fact]
    public async Task RateChange_OnAtempoStream_ComposesBaseAndRateIntoTheNewSeek()
    {
        // Listening at 1.5x from content position 20min (base 20min, rate 1500):
        // the device reports 20min of STREAM (= 30min of content covered), so the
        // content position is 20 + 30 = 50min and the re-launch seeks THERE.
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(
            DeviceId, episode.Id.ToString(), 20 * 60 * 1000, enqueued: false, ratePerMille: 1500);

        var handler = CreateHandler();
        Context context = CreatePlayingContext(episode, offsetMs: 20 * 60 * 1000);
        var user = TestHelpers.CreateTestUser();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("doppia velocità", "2000")), context, user, CreateSession(episode), CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/2000/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Contains($"start={TimeSpan.FromMinutes(50).Ticks}", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Faster_CyclesFromTheActiveScopeRate_NotTheStandingPreference()
    {
        // Scope rate 1500; standing preference 750 (stale): a "faster" steps the
        // ACTIVE rate (1500 -> 1750), not the preference.
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(DeviceId, episode.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = 750;

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("più veloce", PlaybackSpeed.FasterId)),
            CreatePlayingContext(episode, offsetMs: 60_000),
            user,
            CreateSession(episode),
            CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/1750/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(1750, user.PodcastSpeedPerMille);
    }

    [Fact]
    public async Task Slower_AtBottomRate_StaysClampedAndSpeaksNormal()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(DeviceId, episode.Id.ToString(), 0, enqueued: false, ratePerMille: 750);

        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("più lentamente", PlaybackSpeed.SlowerId)),
            CreatePlayingContext(episode, offsetMs: 60_000),
            user,
            CreateSession(episode),
            CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        // 750 - 1 step clamps at 750; rate 1000+750-ladder bottom is 750.
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/750/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Faster_WithNoScopeRate_SeedsFromTheStandingPreference()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = 1500;

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("più veloce", PlaybackSpeed.FasterId)),
            CreatePlayingContext(episode, offsetMs: 60_000),
            user,
            CreateSession(episode),
            CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/1750/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalSpeed_LaunchesThePlainStreamAndPersistsThePreference()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(DeviceId, episode.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = 1500;

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("velocità normale", "1000")),
            CreatePlayingContext(episode, offsetMs: 5 * 60 * 1000),
            user,
            CreateSession(episode),
            CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        // Rate 1000 never rides the speed endpoint: the codec-routed/static URL.
        Assert.DoesNotContain("audio-speed", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Contains($"/Audio/{episode.Id}/stream", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        // The 1.5x stream's 5:00 of stream offset covered 7:30 of content, and the
        // static directive carries the CONTENT position.
        Assert.Equal((int)TimeSpan.FromMinutes(7.5).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(1000, user.PodcastSpeedPerMille);
    }

    [Fact]
    public async Task ConfirmSpeech_NamesTheRate()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        var handler = CreateHandler();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("uno e mezzo", "1500")),
            CreatePlayingContext(episode, offsetMs: 0),
            TestHelpers.CreateTestUser(),
            CreateSession(episode),
            CancellationToken.None);

        Assert.Contains("Velocità uno e mezzo", SpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PositionAtOrBeyondRuntime_FailClosedClampRestartsFromZero()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLaunchBase(DeviceId, episode.Id.ToString(), 0, enqueued: false, ratePerMille: 1000);

        var handler = CreateHandler();
        Context context = CreatePlayingContext(episode, offsetMs: 61 * 60 * 1000); // past the runtime

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("uno e mezzo", "1500")), context, TestHelpers.CreateTestUser(), CreateSession(episode), CancellationToken.None);

        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.DoesNotContain("start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    // ---- the JF-632 VideoApp-medium refusal ----

    [Fact]
    public async Task VideoAppRoutedLedger_RefusesHonestlyInsteadOfReLaunching()
    {
        Audio episode = CreateEpisode(60);
        SetupItemLookup(episode);
        _queueManager.RecordLastPlayed(DeviceId, episode.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);

        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("uno e mezzo", "1500")),
            CreatePlayingContext(episode, offsetMs: 60_000),
            user,
            CreateSession(episode),
            CancellationToken.None);

        // No re-launch directive: the refusal carries AudioPlayer.Stop (the
        // JF-564/JF-632 shape) and the honest line.
        Assert.Null(TestHelpers.GetPlayDirective(response));
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
        Assert.Contains("non posso cambiare la velocità", SpeechText(response), StringComparison.Ordinal);
        Assert.Null(user.PodcastSpeedPerMille);
    }

    /// <summary>
    /// JF-636 review: an audiobook (the multi-chapter concat HLS timeline, also on
    /// the flat-AudioPlayer path when NativeControlsForBooks is off) refuses with a
    /// plain Tell: the item-keyed atempo re-launch cannot span the concat timeline,
    /// and the legitimately playing book must NOT be stopped for the refusal.
    /// </summary>
    [Fact]
    public async Task AudiobookItem_RefusesWithPlainTell_WithoutStoppingPlayback()
    {
        var book = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Long book",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromHours(8).Ticks
        };
        SetupItemLookup(book);

        var handler = CreateHandler();
        var user = TestHelpers.CreateTestUser();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(RateSlot("uno e mezzo", "1500")),
            CreatePlayingContext(book, offsetMs: 60_000),
            user,
            CreateSession(book),
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Stop"));
        Assert.Contains("audiolibri", SpeechText(response), StringComparison.Ordinal);
        Assert.Null(user.PodcastSpeedPerMille);
    }

    private static string SpeechText(SkillResponse response)
        => (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
}
