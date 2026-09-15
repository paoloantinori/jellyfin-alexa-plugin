using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-522: pins the item-absolute provenance contract of every playback-position
/// store the AudioPlayer event writers feed, for a transcode-routed Movie/Episode
/// resumed through the audio-only transcode (the only shape where the two
/// timelines diverge; every other item has launch base 0 and raw ==
/// item-absolute). The writers compose the stream's launch-scoped base at write
/// time into all four stores: the server session PlayState
/// (OnPlaybackStopped/OnPlaybackStart reports), DeviceQueue.CurrentPositionTicks,
/// DeviceQueue.ItemPositionState, and the Jellyfin UserData cross-client fill.
/// Only Amazon's context offsets stay stream-relative (rebased at the reader).
/// History: born as the Phase 1 characterization suite pinning the pre-JF-522
/// raw regime (green on arrival), then re-pinned in the same change.
/// </summary>
[Collection("Plugin")]
public class PlaybackPositionProvenanceTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly DeviceQueueManager? _previousPluginQueueManager;

    public PlaybackPositionProvenanceTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("position-provenance-tests", _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "position-provenance-tests");

        // The PlaybackStarted promote/compose and the enqueue directive's chokepoint
        // record run through Plugin.Instance when the handler was constructed without
        // an injected manager; point the plugin at this suite's manager and restore
        // the previous value on dispose (the temp dir is owned by the registered sweep).
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

    private static long MinutesToTicks(double minutes) => TimeSpan.FromMinutes(minutes).Ticks;

    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private static TestHelpers.TestEpisodeWithStreams Eac3Episode(Guid id)
        => new("Ribs", id, TestHelpers.TestStream(MediaStreamType.Video, "h264"), TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);

    private static AudioPlayerRequest EventRequest(string type, Guid itemId, long offsetMs)
        => new()
        {
            Type = type,
            Token = itemId.ToString(),
            OffsetInMilliseconds = offsetMs
        };

    private PlaybackStoppedEventHandler CreateStopHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _queueManager,
        _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

    private PlaybackStartedEventHandler CreateStartHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _fx.LibraryManager.Object);

    private PlaybackFinishedEventHandler CreateFinishedHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

    private PlaybackNearlyFinishedEventHandler CreateNearlyFinishedHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
        _fx.UserManager.Object, _fx.LoggerFactory, _queueManager);

    // ========== Store 1+2+3+4: PlaybackStopped persists the item-absolute position everywhere ==========

    /// <summary>
    /// The core writer pin (re-pinned by the JF-522 Phase 2 flip): the device stopped a
    /// transcode-launched stream 5:00 into a stream minted at ?start=20:00; the true
    /// item-absolute position is 25:00. The handler composes the stream's ACTIVE
    /// launch base into every store it feeds: the server stop report,
    /// DeviceQueue.CurrentPositionTicks, ItemPositionState, and the UserData
    /// cross-client fill all carry 25:00 (the raw-regime version of this test pinned
    /// the raw 5:00 everywhere).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_TranscodeLaunchedStream_PersistsItemAbsolutePositionInAllFourStores()
    {
        var id = Guid.NewGuid();
        long rawTicks = MinutesToTicks(5);
        long itemAbsoluteTicks = MinutesToTicks(25);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(Eac3Episode(id));
        _fx.SetupUserMock();
        UserItemData? saved = null;
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 });
        _fx.UserDataManager
            .Setup(u => u.SaveUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()))
            .Callback((Jellyfin.Database.Implementations.Entities.User _, BaseItem _, UserItemData data, UserDataSaveReason _, CancellationToken _) => saved = data);

        await CreateStopHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStopped", id, MinutesToMs(5)),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        // Store 1: the server stop report carries the item-absolute position.
        _fx.SessionManager.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == id && i.PositionTicks == itemAbsoluteTicks)),
            Times.Once);

        // Store 2: the device queue's resume-after-pause position.
        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(itemAbsoluteTicks, queue.CurrentPositionTicks);

        // Store 3: the per-item position state ("N"-format key).
        Assert.Equal(itemAbsoluteTicks, queue.ItemPositionState[id.ToString("N")]);

        // Store 4: the UserData cross-client fill writes the same item-absolute ticks.
        Assert.NotNull(saved);
        Assert.Equal(itemAbsoluteTicks, saved!.PlaybackPositionTicks);

        // The store-consistency invariant (the JF-521 equality premise, re-pinned): the
        // UserData fill tick-equals the device's own per-item recorded position - both
        // written by this stop event, both item-absolute since JF-522.
        Assert.Equal(
            _queueManager.GetOrCreateQueue(DeviceId).ItemPositionState[id.ToString("N")],
            saved.PlaybackPositionTicks);
    }

    /// <summary>
    /// The no-launch shape (plain play, pre-deploy launch, wiped queue file): no base is
    /// recorded, and the raw offset IS the item-absolute position. This store value must
    /// NOT change in the migration (base 0 composes to the same number).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_NoRecordedLaunch_PersistsRawOffset_Unchanged()
    {
        var id = Guid.NewGuid();

        await CreateStopHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStopped", id, 30000),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(TimeSpan.FromMilliseconds(30000).Ticks, queue.CurrentPositionTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(30000).Ticks, queue.ItemPositionState[id.ToString("N")]);
    }

    /// <summary>
    /// The writer-side stale-base guard (a genuinely new JF-522 invariant): a composition
    /// that passes the item runtime is never persisted. Here the scope says base 20:00 on
    /// a 25:00-runtime item while the device reports a 10:00 offset (composed 30:00 >
    /// runtime), the shape a stale scope produces when a same-item relaunch's stop arrives
    /// late or a promote never paired; the raw 10:00 wins as the conservative truth.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_CompositionPassesItemRuntime_PersistsRawOffset()
    {
        var id = Guid.NewGuid();
        var episode = Eac3Episode(id);
        episode.RunTimeTicks = MinutesToTicks(25);
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(episode);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        await CreateStopHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStopped", id, MinutesToMs(10)),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(MinutesToTicks(10), queue.CurrentPositionTicks);
        Assert.Equal(MinutesToTicks(10), queue.ItemPositionState[id.ToString("N")]);
        _fx.SessionManager.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == id && i.PositionTicks == MinutesToTicks(10))),
            Times.Once);
    }

    /// <summary>
    /// Composite sleep-timer tokens ("{guid}|sleep:{ticks}", JF-447) resolve through the
    /// shared codec on the writer side too: the stop composes with the parsed item's
    /// active launch base and persists the item-absolute position under the CLEAN id.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_CompositeSleepToken_ComposesWithParsedItemsBase()
    {
        var id = Guid.NewGuid();
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        var sleepRequest = EventRequest("AudioPlayer.PlaybackStopped", id, MinutesToMs(5));
        sleepRequest.Token = $"{id}|sleep:{DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks}";
        await CreateStopHandler().HandleAsync(
            sleepRequest,
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(MinutesToTicks(25), queue.ItemPositionState[id.ToString("N")]);
    }

    // ========== Store 1: the start/finish reports ==========

    /// <summary>
    /// The started-report pin (re-pinned by the JF-522 flip): a transcode-launched stream
    /// carries directive offset 0, and the started report now composes the stream's
    /// 20:00 launch base, so the server PlayState says the item plays from 20:00 (the
    /// raw regime reported 0). PlayState readers (MediaInfo, SkipForwardBack, the
    /// resume tail fallback 2) see the item-absolute truth mid-stream.
    /// </summary>
    [Fact]
    public async Task PlaybackStarted_TranscodeLaunchedStream_ReportsItemAbsolutePosition()
    {
        var id = Guid.NewGuid();
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        await CreateStartHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStarted", id, 0),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        _fx.SessionManager.Verify(
            s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == id && i.PositionTicks == MinutesToTicks(20))),
            Times.Once);
    }

    /// <summary>
    /// The finished-report pin (re-pinned by the JF-522 flip): at natural end-of-stream
    /// the finish event reports the item-absolute end position (launch base + raw
    /// offset = the item runtime for a fully-watched stream), which the writer-side
    /// runtime guard admits (only strictly-past-runtime compositions are rejected).
    /// </summary>
    [Fact]
    public async Task PlaybackFinished_TranscodeLaunchedStream_ReportsItemAbsolutePosition()
    {
        var id = Guid.NewGuid();
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        await CreateFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackFinished", id, MinutesToMs(10)),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        _fx.SessionManager.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == id && i.PositionTicks == MinutesToTicks(30))),
            Times.Once);
    }

    // ========== Store 2: the NearlyFinished recovery-pointer refresh ==========

    /// <summary>
    /// The third writer of CurrentPositionTicks (re-pinned by the JF-522 review):
    /// a SEQUENTIAL advance moves the pointer to the next item but must NOT persist
    /// the finishing item's position under it - the resume tail now mints persisted
    /// values instead of dropping them, so a misattributed entry would start the next
    /// item deep into its runtime. The unknown position resets to 0; the next stop
    /// writes the real one (the pre-JF-522 code wrote the finishing item's raw offset
    /// under the next pointer, harmlessly dropped by the old reader).
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_SequentialAdvance_ResetsPositionInsteadOfMisattributing()
    {
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var session = CreateSession();
        session.PlayState = new PlayerStateInfo();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentId, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId }, new() { Id = nextId } };
        _fx.LibraryManager.Setup(lm => lm.GetItemById(nextId))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = nextId, Name = "Track 2" });
        _queueManager.SetQueue(DeviceId, new List<string> { currentId.ToString(), nextId.ToString() }, 0);
        _queueManager.GetOrCreateQueue(DeviceId).CurrentPositionTicks = TimeSpan.FromMilliseconds(90).Ticks;

        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState { Token = currentId.ToString(), OffsetInMilliseconds = 90000 };

        await CreateNearlyFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackNearlyFinished", currentId, 90000),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(nextId.ToString(), queue.CurrentItemId);
        Assert.Equal(0L, queue.CurrentPositionTicks);
    }

    /// <summary>
    /// The wrapped/repeat-one complement: when the enqueue targets the SAME item the
    /// pointer names, the refresh is genuinely attributable and composes the item-
    /// absolute position (launch base + context offset) under the surviving pointer.
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_SameItemAdvance_RefreshesComposedPosition()
    {
        var id = Guid.NewGuid();
        var session = CreateSession();
        session.PlayState = new PlayerStateInfo { RepeatMode = RepeatMode.RepeatOne };
        session.FullNowPlayingItem = Eac3Episode(id);
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = id } };
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(Eac3Episode(id));
        _queueManager.SetQueue(DeviceId, new List<string> { id.ToString() }, 0);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState { Token = id.ToString(), OffsetInMilliseconds = MinutesToMs(2) };

        await CreateNearlyFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackNearlyFinished", id, 0),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(id.ToString(), queue.CurrentItemId);
        Assert.Equal(MinutesToTicks(22), queue.CurrentPositionTicks);
    }

    // ========== The AC#2 hazard, closed: the launch scope survives the wrapped-queue resolve ==========

    /// <summary>
    /// The wrapped-queue clobber the JF-521 rejection documented, now CLOSED by the
    /// launch-scoped capture (the Phase 1 version of this test pinned the OLD hazard:
    /// the last-RESOLVE ledger was zeroed mid-playback). A repeat-one queue resolving
    /// its next item during NearlyFinished enqueues the SAME item at base 0; that
    /// enqueue writes the PENDING scope, and the running stream's ACTIVE base (20:00)
    /// survives until the item's next PlaybackStarted promotes the pending entry. A
    /// stop arriving in that window (pause after NearlyFinished, before the wrap)
    /// therefore composes with the 20:00 base it was launched with, never 0.
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_RepeatOneWrappedQueue_ActiveBaseSurvivesTheEnqueue()
    {
        var id = Guid.NewGuid();
        var session = CreateSession();
        session.PlayState = new PlayerStateInfo { RepeatMode = RepeatMode.RepeatOne };
        session.FullNowPlayingItem = Eac3Episode(id);
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = id } };
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(Eac3Episode(id));
        _queueManager.SetQueue(DeviceId, new List<string> { id.ToString() }, 0);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState { Token = id.ToString(), OffsetInMilliseconds = MinutesToMs(2) };

        await CreateNearlyFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackNearlyFinished", id, 0),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // The enqueue of the wrapped next item (the same episode, base 0) landed in the
        // PENDING scope; the running stream's ACTIVE base is untouched.
        Assert.Equal(MinutesToMs(20), _queueManager.GetActiveLaunchBase(DeviceId, id.ToString()));
        Assert.Equal(0, _queueManager.GetOrCreateQueue(DeviceId).PendingLaunchBaseMs[id.ToString("N")]);

        // And the promote at the wrapped stream's start retires the 20:00 base for the
        // new 0-base stream (repeat-one semantics: the track plays again from its start).
        await CreateStartHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStarted", id, 0),
            context,
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);
        Assert.Equal(0, _queueManager.GetActiveLaunchBase(DeviceId, id.ToString()));
    }

    // ========== Compensating reader chain, end to end (the shape Phase 2 must preserve) ==========

    /// <summary>
    /// The full old-regime chain pin: the device stopped a base-20:00 transcode stream
    /// at raw 5:00 (persisted raw everywhere), and the resume tail REBASES the
    /// AudioPlayer-context offset against the recorded base, minting the correct
    /// item-absolute 25:00. Post-migration the stores hold 25:00 directly and the
    /// context-seed rebase must keep producing 25:00 (Amazon's context offset stays
    /// stream-relative by platform contract, so this composition survives).
    /// </summary>
    [Fact]
    public async Task ResumeTail_TranscodeLaunchedStop_RebasesContextOffsetAgainstRecordedBase()
    {
        var id = Guid.NewGuid();
        var episode = Eac3Episode(id);
        var session = CreateSession();
        session.PlayState = new PlayerStateInfo();
        session.FullNowPlayingItem = episode;
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(episode);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState
        {
            Token = id.ToString(),
            OffsetInMilliseconds = MinutesToMs(5),
            PlayerActivity = "IDLE"
        };

        SkillResponse response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        AudioPlayerPlayDirective directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Contains(
            $"?start={TimeSpan.FromMinutes(25).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }
}
