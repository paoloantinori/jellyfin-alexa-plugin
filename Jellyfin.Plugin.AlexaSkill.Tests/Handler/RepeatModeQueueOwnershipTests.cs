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
}
