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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-906: the per-device queue is the ONE durable owner of the repeat mode, and
/// every repeat read on the event paths is queue-first. The wipe these pins hold
/// against is device-verified and source-verified at tags v10.11.8/v12.0/v12.1:
/// SessionManager.UpdateNowPlayingItem sets session.PlayState.RepeatMode from
/// EVERY playback report, and OnPlaybackStart arms SessionInfo.StartAutomaticProgress,
/// a 1-second timer that re-fires the START-time snapshot (RepeatMode captured
/// before any mid-track loop command, i.e. RepeatNone) for as long as the stream
/// plays. The JF-280 in-memory write therefore survives at most ~1s into the
/// track, which is exactly the live shape ("attiva il loop" spoke repeat-one,
/// the queue advanced at track end, corr 477ed915). The session wipe simulated
/// below is that server behavior, not an invented hazard.
/// RED on the pre-JF-906 tree: ApplyRepeatModeAsync never wrote the queue and
/// ResolveNextItemId read the session PlayState, so under the wipe the queue
/// mode stayed "None" and the resolution advanced sequentially.
/// The session-fallback half of the contract (no device queue: the session
/// PlayState decides) stays pinned by the manager-less RepeatOne/RepeatAll rows
/// in GaplessPlaybackTests.
/// The raw "One"/"All"/"None" literals in these pins are DELIBERATE: they pin
/// the persisted wire vocabulary itself (an assert through
/// DeviceQueueManager.ToQueueRepeatModeString would be tautological); the
/// incidental fixtures in PlaybackPositionProvenanceTests seed through the
/// helper instead.
/// </summary>
[Collection("Plugin")]
public class RepeatModeQueueOwnershipTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IUserDataManager> _userDataManager = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    /// <summary>ServerAddress feeds the enqueue stream URLs (the GaplessPlaybackTests shape).</summary>
    private readonly PluginConfiguration _config = new() { ServerAddress = "http://localhost:8096" };

    private readonly string _device = $"jf906-{Guid.NewGuid():N}";
    private readonly DeviceQueueManager _queues;

    public RepeatModeQueueOwnershipTests()
    {
        _sessionManager
            .Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        _sessionManager
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);
        _queues = TestHelpers.CreateDeviceQueueManager($"jf906-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        _queues.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManager.Object, _loggerFactory);

    private static IntentRequest IntentRequestFor(string intentName) =>
        new() { Intent = new Intent { Name = intentName }, Locale = "it-IT", RequestId = "test-req" };

    /// <summary>
    /// The verified server-side wipe: any progress report overwrites
    /// session.PlayState.RepeatMode, and the start-report's 1-second
    /// automatic-progress timer re-fires the launch-time snapshot (RepeatNone)
    /// for the whole track. Called AFTER a loop command to model the state the
    /// resolver sees at the real track end, minutes later.
    /// </summary>
    private static void SimulateServerProgressReportWipe(SessionInfo session)
        => session.PlayState!.RepeatMode = RepeatMode.RepeatNone;

    private LoopSongOnIntentHandler CreateLoopSongOnHandler()
        => new(_sessionManager.Object, _config, _libraryManager.Object, _loggerFactory, _queues);

    private LoopOnIntentHandler CreateLoopAllOnHandler()
        => new(_sessionManager.Object, _config, _libraryManager.Object, _loggerFactory, _queues);

    private LoopOffIntentHandler CreateLoopOffHandler()
        => new(_sessionManager.Object, _config, _libraryManager.Object, _loggerFactory, _queues);

    private PlaybackNearlyFinishedEventHandler CreateNearlyFinishedHandler()
        => new(_sessionManager.Object, _config, _libraryManager.Object, _userManager.Object, _loggerFactory, _queues);

    private PlaybackStoppedEventHandler CreateStoppedHandler()
        => new(
            _sessionManager.Object, _config, _loggerFactory, _queues,
            _libraryManager.Object, _userManager.Object, _userDataManager.Object);

    private static AudioPlayerPlayDirective? EnqueueDirective(SkillResponse response)
        => response.Response?.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();

    /// <summary>
    /// THE JF-906 pin (the device matrix shape: bare built-in loop then track
    /// end): "attiva il loop" must write the per-device queue (the durable
    /// owner), and when the server's progress-report wipe has taken the session
    /// PlayState back to RepeatNone by track end, PlaybackNearlyFinished still
    /// resolves from the queue mode One and re-enqueues the SAME item.
    /// </summary>
    [Fact]
    public async Task LoopOn_WritesQueueModeOne_WipedSessionStillRepeatsSameTrack()
    {
        var track1 = TestHelpers.CreateSong("Looped Track");
        var track2 = TestHelpers.CreateSong("Next Track");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        Context context = TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device);
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("One", _queues.GetQueue(_device)!.RepeatMode);
        Assert.Equal(RepeatMode.RepeatOne, session.PlayState!.RepeatMode);

        SimulateServerProgressReportWipe(session);

        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        // The same-item re-enqueue mints a launch-generation suffix ("{guid}|launch:N",
        // the JF-447 codec); the item identity is the parsed id.
        Assert.True(
            StreamTokenCodec.TryGetItemId(directive!.AudioItem.Stream.Token, out Guid repeatedId)
            && repeatedId == track1.Id,
            $"expected the same track {track1.Id} re-enqueued, got {directive.AudioItem.Stream.Token}");
        Assert.Equal(track1.Id.ToString(), directive.AudioItem.Stream.ExpectedPreviousToken);
    }

    /// <summary>
    /// The wipe-window pin through the REAL stop-report path: loop set, then a
    /// PlaybackStopped event for the current track (the full
    /// ReportStopOrderedAsync sequence, position writes and all), then
    /// NearlyFinished. The stop path must leave the queue mode intact and the
    /// resolution must still repeat.
    /// </summary>
    [Fact]
    public async Task LoopOn_ThenRealStopEvent_ThenNearlyFinished_StillRepeats()
    {
        var track1 = TestHelpers.CreateSong("Stop Window Track");
        var track2 = TestHelpers.CreateSong("Stop Window Next");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);
        SimulateServerProgressReportWipe(session);

        await CreateStoppedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStopped", track1.Id.ToString(), offsetMs: 30_000),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("One", _queues.GetQueue(_device)!.RepeatMode);

        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        Assert.True(
            StreamTokenCodec.TryGetItemId(directive!.AudioItem.Stream.Token, out Guid repeatedId)
            && repeatedId == track1.Id,
            $"expected the same track {track1.Id} re-enqueued after the stop event, got {directive.AudioItem.Stream.Token}");
    }

    /// <summary>
    /// RepeatAll through the queue owner under the same wipe: at the LAST queue
    /// position the resolution wraps to the first item instead of ending
    /// playback (the queue-side twin of the GaplessPlaybackTests session pin).
    /// </summary>
    [Fact]
    public async Task LoopAllOn_WipedSession_AtQueueEnd_WrapsToFirst()
    {
        var track1 = TestHelpers.CreateSong("Wrap First");
        var track2 = TestHelpers.CreateSong("Wrap Last");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 1);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track2;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        await CreateLoopAllOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOn),
            TestHelpers.CreateContextWithToken(track2.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("All", _queues.GetQueue(_device)!.RepeatMode);
        SimulateServerProgressReportWipe(session);

        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track2.Id.ToString()),
            TestHelpers.CreateContextWithToken(track2.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        Assert.Equal(track1.Id.ToString(), directive!.AudioItem.Stream.Token);
    }

    /// <summary>
    /// LoopOff clears the queue mode back to None (the same write path), so the
    /// next boundary advances sequentially again. The round-trip contract: the
    /// queue is the one owner both directions.
    /// </summary>
    [Fact]
    public async Task LoopOff_ClearsQueueMode_NearlyFinishedAdvancesSequentially()
    {
        var track1 = TestHelpers.CreateSong("Clear Me");
        var track2 = TestHelpers.CreateSong("Then Me");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        Context context = TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device);
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.Equal("One", _queues.GetQueue(_device)!.RepeatMode);

        await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("None", _queues.GetQueue(_device)!.RepeatMode);
        SimulateServerProgressReportWipe(session);

        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        Assert.Equal(track2.Id.ToString(), directive!.AudioItem.Stream.Token);
    }

    /// <summary>
    /// The JF-906 belt: the playback-START report carries the repeat mode from
    /// the queue owner, not the session. OnPlaybackStart arms the server's
    /// 1-second automatic-progress timer with this report's snapshot, so a
    /// session-sourced value would re-wipe the server PlayState every second of
    /// the NEXT track; the queue-sourced one keeps the dashboard (and any
    /// displacement-restore report) coherent with the resolver's authority.
    /// </summary>
    [Fact]
    public async Task PlaybackStartReport_CarriesQueueRepeatMode_NotWipedSessionValue()
    {
        var track1 = TestHelpers.CreateSong("Belt Track");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString() }, currentIndex: 0);
        _queues.SetRepeatMode(_device, "One");

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = track1.Id } };
        SimulateServerProgressReportWipe(session); // the wiped shape the start report must not trust

        var startedHandler = new PlaybackStartedEventHandler(
            _sessionManager.Object, _config, _loggerFactory, _libraryManager.Object, _queues);
        await startedHandler.HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStarted", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // JF-425: the report is fire-and-forget; a single Task.Yield only works
        // while the dispatch stays synchronous up to the first await (the review
        // round's flake note), so poll BOUNDED for the observed invocation.
        PlaybackStartInfo? reported = null;
        for (int attempt = 0; attempt < 100 && reported == null; attempt++)
        {
            reported = _sessionManager
                .Invocations
                .Select(i => i.Arguments.OfType<PlaybackStartInfo>().FirstOrDefault())
                .FirstOrDefault(i => i != null);
            if (reported == null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(reported);
        Assert.Equal(RepeatMode.RepeatOne, reported!.RepeatMode);
    }

    // ==== The code-review round's stale-queue findings (empirically probed) ====

    /// <summary>
    /// The review round's probe-verified leak: a looped playback leaves its mode
    /// on the persisted device queue, and the single-song play family (PlaySong
    /// and siblings) never calls SetQueue, so the stale queue outlives it. The
    /// resolver's COHERENCE GATE must answer RepeatNone for a queue that does
    /// not contain the playing item: the later single song must NOT re-enqueue
    /// itself forever. RED on the ungated queue-first tree (the probe: the real
    /// NearlyFinished handler re-enqueued the unrelated song); GREEN on the
    /// original session-read tree and on the gated tree.
    /// </summary>
    [Fact]
    public async Task StaleLoopedQueue_DoesNotGovernLaterSingleSongPlay()
    {
        var staleAlbumTrack = TestHelpers.CreateSong("Stale Album Track");
        var laterSong = TestHelpers.CreateSong("Later Single Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, staleAlbumTrack, laterSong);

        // The residue an earlier looped album playback left behind (persists to disk).
        _queues.SetQueue(_device, new List<string> { staleAlbumTrack.Id.ToString() }, currentIndex: 0);
        _queues.SetRepeatMode(_device, "One");

        // The later single-song play: session queue replaced with [song] (the
        // PlaybackLaunchBuilder single-item shape), session wiped as the server does.
        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = laterSong;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = laterSong.Id } };
        SimulateServerProgressReportWipe(session);

        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", laterSong.Id.ToString()),
            TestHelpers.CreateContextWithToken(laterSong.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response!);
    }

    /// <summary>
    /// The belt's foreign-queue arm: the start report for a playback the queue
    /// does not describe must carry RepeatNone, never the stale queue mode NOR
    /// the equally stale session copy (the 1-second automatic-progress timer
    /// would perpetuate either for the whole track and poison the session
    /// fallback). RED on both the session-read tree and the ungated queue-first
    /// tree (both report RepeatOne here).
    /// </summary>
    [Fact]
    public async Task StaleLoopedQueue_StartReportAnswersNone_NotStaleModes()
    {
        var staleAlbumTrack = TestHelpers.CreateSong("Stale Album Track 2");
        var laterSong = TestHelpers.CreateSong("Later Single Song 2");
        TestHelpers.SetupLibraryResolves(_libraryManager, staleAlbumTrack, laterSong);
        _queues.SetQueue(_device, new List<string> { staleAlbumTrack.Id.ToString() }, currentIndex: 0);
        _queues.SetRepeatMode(_device, "One");

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = laterSong;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = laterSong.Id } };
        session.PlayState!.RepeatMode = RepeatMode.RepeatOne; // the stale session copy

        var startedHandler = new PlaybackStartedEventHandler(
            _sessionManager.Object, _config, _loggerFactory, _libraryManager.Object, _queues);
        await startedHandler.HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStarted", laterSong.Id.ToString()),
            TestHelpers.CreateContextWithToken(laterSong.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        PlaybackStartInfo? reported = null;
        for (int attempt = 0; attempt < 100 && reported == null; attempt++)
        {
            reported = _sessionManager
                .Invocations
                .Select(i => i.Arguments.OfType<PlaybackStartInfo>().FirstOrDefault())
                .FirstOrDefault(i => i != null);
            if (reported == null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(reported);
        Assert.Equal(RepeatMode.RepeatNone, reported!.RepeatMode);
    }

    /// <summary>
    /// The review round's empty-queue artifact finding: writing the mode onto a
    /// device that had NO queue must not plant a durable mode-only artifact; the
    /// write seeds the current item so the loop governs THIS playback (and reads
    /// coherently at its boundaries). The membership assertions are RED on both
    /// the original tree (no write at all) and the ungated write (empty queue).
    /// </summary>
    [Fact]
    public async Task SessionOnlyLoop_SeedsQueueMembership_AndRepeats()
    {
        var song = TestHelpers.CreateSong("Session Only Loop Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        // NO SetQueue: the PlaySong session-only shape (no device queue exists).

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = song;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };

        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var queue = _queues.GetQueue(_device);
        Assert.NotNull(queue);
        Assert.Equal("One", queue!.RepeatMode);
        Assert.Equal(new List<string> { song.Id.ToString() }, queue.ItemIds);

        SimulateServerProgressReportWipe(session);
        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", song.Id.ToString()),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        Assert.True(
            StreamTokenCodec.TryGetItemId(directive!.AudioItem.Stream.Token, out Guid repeatedId)
            && repeatedId == song.Id,
            $"expected the same song {song.Id} re-enqueued, got {directive.AudioItem.Stream.Token}");
    }

    /// <summary>
    /// The loop write over a FOREIGN queue (an earlier playback's membership,
    /// without the current item): the write inserts the current item (the
    /// AfterCurrent-with-self front fallback) so the loop governs this playback,
    /// while PRESERVING the existing members (an AddToQueue seed must survive
    /// the loop command; a wholesale SetQueue replace would clobber it). The
    /// membership assertions are RED on both prior trees.
    /// </summary>
    [Fact]
    public async Task SessionOnlyLoop_OverForeignQueue_InsertsCurrentAndPreservesMembers()
    {
        var member1 = TestHelpers.CreateSong("Foreign Member 1");
        var member2 = TestHelpers.CreateSong("Foreign Member 2");
        var song = TestHelpers.CreateSong("Loop Over Foreign Queue Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, member1, member2, song);
        _queues.SetQueue(
            _device, new List<string> { member1.Id.ToString(), member2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = song;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };

        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var queue = _queues.GetQueue(_device)!;
        Assert.Equal("One", queue.RepeatMode);
        Assert.Equal(
            new List<string> { song.Id.ToString(), member1.Id.ToString(), member2.Id.ToString() },
            queue.ItemIds);

        SimulateServerProgressReportWipe(session);
        var response = await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", song.Id.ToString()),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        AudioPlayerPlayDirective? directive = EnqueueDirective(response!);
        Assert.NotNull(directive);
        Assert.True(
            StreamTokenCodec.TryGetItemId(directive!.AudioItem.Stream.Token, out Guid repeatedId)
            && repeatedId == song.Id,
            $"expected the same song {song.Id} re-enqueued, got {directive.AudioItem.Stream.Token}");
    }

    // ==== JF-910: the LoopOff enqueue race (the 33s-remaining shape) ====

    /// <summary>
    /// THE JF-910 pin (the device incident: loop enabled, NearlyFinished fired
    /// inside the ~30s pre-enqueue window and enqueued the same track, loop
    /// disabled 6s later, the track repeated anyway): the mode clear cannot
    /// remove an already-delivered ENQUEUE, so LoopOff must also issue
    /// ClearQueue(CLEAR_ENQUEUED) when the JF-691 enqueue record shows a
    /// same-item repeat still pending after the CURRENT stream. CLEAR_ENQUEUED
    /// (never CLEAR_ALL) is load-bearing: it leaves the currently-playing
    /// stream alone, so the finishing track plays out and playback stops there.
    /// </summary>
    [Fact]
    public async Task LoopOff_AfterRepeatEnqueued_IssuesClearQueueEnqueued()
    {
        var track1 = TestHelpers.CreateSong("Race Track");
        var track2 = TestHelpers.CreateSong("Race Next");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        Context context = TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device);
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The race: NearlyFinished pre-enqueues the same-item repeat (this also
        // records the JF-691 enqueue pair: afterToken = the finishing stream).
        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("None", _queues.GetQueue(_device)!.RepeatMode);
        ClearQueueDirective? clear = offResponse.Response?.Directives.OfType<ClearQueueDirective>().FirstOrDefault();
        Assert.NotNull(clear);
        Assert.Equal(ClearBehavior.ClearEnqueued, clear!.ClearBehavior);

        // The retire-side record clear (the JF-691 twin of the directive): the
        // stale after-token would make PlaybackFinished's EnqueuedForThisBoundary
        // pass for the finishing boundary and keep the session open over a
        // device whose continuation was just cleared.
        Assert.Null(_queues.GetQueue(_device)!.LastEnqueueAfterToken);
        Assert.Null(_queues.GetQueue(_device)!.LastEnqueueNextItemId);
    }

    /// <summary>
    /// The safety leg: a LoopOff with NOTHING pending on the device (loop on,
    /// track not near its end yet, no enqueue ever issued) stays a pure speech
    /// response; issuing ClearQueue speculatively would be a behavior change
    /// with no race to fix.
    /// </summary>
    [Fact]
    public async Task LoopOff_BeforeAnyEnqueue_NoClearQueueDirective()
    {
        var track1 = TestHelpers.CreateSong("No Race Track");
        var track2 = TestHelpers.CreateSong("No Race Next");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        Context context = TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device);
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal("None", _queues.GetQueue(_device)!.RepeatMode);
        Assert.Empty(offResponse.Response?.Directives.OfType<ClearQueueDirective>() ?? Enumerable.Empty<ClearQueueDirective>());
    }

    /// <summary>
    /// The sequential-advance leg: a legit gapless enqueue of the NEXT track
    /// (RepeatAll, or any enqueue recorded before the loop was enabled) must
    /// SURVIVE a LoopOff. Only a same-item pending enqueue is the repeat
    /// artifact; clearing here would break ordinary queue advancement.
    /// </summary>
    [Fact]
    public async Task LoopAllOff_AfterSequentialEnqueue_NoClearQueueDirective()
    {
        var track1 = TestHelpers.CreateSong("Sequential First");
        var track2 = TestHelpers.CreateSong("Sequential Second");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        await CreateLoopAllOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOn),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // RepeatAll at queue position 0 enqueues the NEXT item, not the same one.
        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOff),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Empty(offResponse.Response?.Directives.OfType<ClearQueueDirective>() ?? Enumerable.Empty<ClearQueueDirective>());
    }

    /// <summary>
    /// The mode-gate leg: a LoopOff spoken with NO loop active (the queue mode
    /// already None, a sequential enqueue pending from the normal advance)
    /// answers speech only; the pending NEXT track is not a repeat artifact
    /// and must survive.
    /// </summary>
    [Fact]
    public async Task LoopOff_NoLoopActive_AfterSequentialEnqueue_NoClearQueueDirective()
    {
        var track1 = TestHelpers.CreateSong("Mode Gate First");
        var track2 = TestHelpers.CreateSong("Mode Gate Second");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        // No loop command at all: the plain sequential advance enqueues track2.
        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Empty(offResponse.Response?.Directives.OfType<ClearQueueDirective>() ?? Enumerable.Empty<ClearQueueDirective>());
    }

    /// <summary>
    /// The composite-generation variant of the race pin: on a second-or-later
    /// repetition the finishing stream's token carries the JF-655 launch
    /// generation ("{guid}|launch:n", the live incident family), and both the
    /// NearlyFinished event and the LoopOff request echo it. The gate's
    /// after-token leg compares that raw form Ordinal against the record, so a
    /// refactor canonicalizing only ONE side must fail here, not silently
    /// reintroduce the race on composite generations.
    /// </summary>
    [Fact]
    public async Task LoopOff_AfterRepeatEnqueued_CompositeLaunchToken_StillClears()
    {
        var track1 = TestHelpers.CreateSong("Composite Race Track");
        var track2 = TestHelpers.CreateSong("Composite Race Next");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        string compositeToken = track1.Id + "|launch:2";
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn),
            TestHelpers.CreateContextWithToken(compositeToken, _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", compositeToken),
            TestHelpers.CreateContextWithToken(compositeToken, _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff),
            TestHelpers.CreateContextWithToken(compositeToken, _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        ClearQueueDirective? clear = offResponse.Response?.Directives.OfType<ClearQueueDirective>().FirstOrDefault();
        Assert.NotNull(clear);
        Assert.Equal(ClearBehavior.ClearEnqueued, clear!.ClearBehavior);
    }

    /// <summary>
    /// The mode-leg isolation (the idempotency window): after the FIRST LoopOff
    /// retired the pending repeat, a SECOND LoopOff must answer speech only.
    /// The first retire also cleared the JF-691 record, so with the record gone
    /// the PRE-WRITE MODE leg is the belt that answers even if a future change
    /// re-populates a same-item record; this pin fails through the mode leg
    /// when that belt is removed.
    /// </summary>
    [Fact]
    public async Task SecondLoopOff_AfterFirstClearedRepeat_IsSpeechOnly()
    {
        var track1 = TestHelpers.CreateSong("Idempotency Track");
        var track2 = TestHelpers.CreateSong("Idempotency Next");
        TestHelpers.SetupLibraryResolves(_libraryManager, track1, track2);
        _queues.SetQueue(_device, new List<string> { track1.Id.ToString(), track2.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1.Id },
            new() { Id = track2.Id },
        };

        Context context = TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device);
        await CreateLoopSongOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", track1.Id.ToString()),
            TestHelpers.CreateContextWithToken(track1.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var firstOff = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(firstOff.Response?.Directives.OfType<ClearQueueDirective>().FirstOrDefault());

        var secondOff = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.AmazonLoopOff), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.Empty(secondOff.Response?.Directives.OfType<ClearQueueDirective>() ?? Enumerable.Empty<ClearQueueDirective>());
    }

    /// <summary>
    /// The accepted same-item shape (the helper doc's contract): RepeatAll over
    /// a SINGLE-item queue wraps to the only member, so the sequential enqueue
    /// IS the same item; disabling that loop must clear it exactly like the
    /// repeat-one artifact (string-indistinguishable, and RepeatAll over one
    /// track is a loop the user is turning off).
    /// </summary>
    [Fact]
    public async Task LoopAllOff_SingleItemQueue_WrappedSameItemEnqueue_IsCleared()
    {
        var only = TestHelpers.CreateSong("Only Wrap Track");
        TestHelpers.SetupLibraryResolves(_libraryManager, only);
        _queues.SetQueue(_device, new List<string> { only.Id.ToString() }, currentIndex: 0);

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = only;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = only.Id } };

        Context context = TestHelpers.CreateContextWithToken(only.Id.ToString(), _device);
        await CreateLoopAllOnHandler().HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOn), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        await CreateNearlyFinishedHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", only.Id.ToString()),
            TestHelpers.CreateContextWithToken(only.Id.ToString(), _device),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var offResponse = await CreateLoopOffHandler().HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOff), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        ClearQueueDirective? clear = offResponse.Response?.Directives.OfType<ClearQueueDirective>().FirstOrDefault();
        Assert.NotNull(clear);
        Assert.Equal(ClearBehavior.ClearEnqueued, clear!.ClearBehavior);
    }
}
