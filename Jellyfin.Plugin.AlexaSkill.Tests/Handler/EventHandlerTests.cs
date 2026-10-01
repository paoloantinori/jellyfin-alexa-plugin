using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for event handlers: PlaybackStarted, Finished, Stopped, Failed,
/// SessionEndedRequest, and ExceptionHandler.
/// </summary>
[Collection("Plugin")]
public class EventHandlerTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;

    public EventHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _queueManager = TestHelpers.CreateDeviceQueueManager("EventHandlerTests");
    }

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end (no post-test straggler).
    public void Dispose() => _queueManager.Dispose();

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    /// <summary>
    /// Context with a fresh unique device ID. The ordering tests must not share
    /// TestHelpers' fixed "test-device": the static PlaybackReportOrdering state
    /// (JF-447) keys per device process-wide, and leftover state for a shared device
    /// changes how a stop classifies (displacement vs real) and skips the
    /// registration the tests exercise.
    /// </summary>
    private static Context CreateContextForFreshDevice() => TestHelpers.CreateTestContext($"jf425-{Guid.NewGuid():N}");

    private PlaybackStartedEventHandler CreateStartHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private PlaybackStoppedEventHandler CreateStopHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);

    private PlaybackFinishedEventHandler CreateFinishedHandler(ILoggerFactory? loggerFactory = null)
        => new(_sessionManagerMock.Object, _config, loggerFactory ?? _loggerFactory);

    private PlaybackFailedEventHandler CreateFailedHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static AudioPlayerRequest CreateAudioPlayerRequest(string type, string? token = null, long offset = 0)
    {
        var request = new AudioPlayerRequest
        {
            Type = type,
            Token = token ?? Guid.NewGuid().ToString(),
            OffsetInMilliseconds = offset
        };
        return request;
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsTrueForPlaybackStarted()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlaybackStarted_CanHandle_ReturnsFalseForOtherTypes()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped");

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackStarted_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", offset: 5000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
    }

    [Fact]
    public async Task PlaybackStarted_Handle_ServerReportStalls_StillResponds()
    {
        // JF-410: OnPlaybackStart stalled 11.3s/20.6s inside Jellyfin on-device (breaching
        // Alexa's ~8s window, INVALID_RESPONSE, "Qualcosa è andato storto"). The keep-alive
        // ack must not wait on the server-side playback report: respond immediately, report
        // in the background.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(gate.Task);

        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted");

        var handleTask = handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var winner = await Task.WhenAny(handleTask, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(handleTask, winner);
        Assert.NotNull(await handleTask);

        gate.TrySetResult();
        _sessionManagerMock.Verify(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
    }

    [Fact]
    public async Task PlaybackStarted_Handle_DoesNotSetShouldEndSessionFalse()
    {
        // JF-299: AudioPlayer.PlaybackStarted responses must NOT set shouldEndSession=false.
        // Amazon rejects it (InvalidResponse "Response may not have shouldEndSession set to
        // false") on every playback. The value must be null (keep-alive) or true. Regression
        // guard for the invalid shouldEndSession=false previously returned here.
        var handler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", offset: 5000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.True(response.Response.ShouldEndSession != false, "PlaybackStarted must not return shouldEndSession=false (Amazon rejects it on AudioPlayer events)");
    }

    [Fact]
    public async Task PlaybackStarted_DelayedReportAfterStop_ReissuesStopToClearZombie()
    {
        // JF-425: the start report is fire-and-forget (JF-410) and its session write lands
        // inside ISessionManager.OnPlaybackStart after an internal await. When the user
        // stop completes while that report is stalled, the delayed report would flip the
        // session back to Playing with the stale now-playing item (zombie position card,
        // MediaInfo, resume fallback). The guard must re-issue the superseding stop so the
        // session ends NOT playing.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        string token = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        var startResponse = await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", token), context, user, CreateSession(), CancellationToken.None);
        Assert.NotNull(startResponse);
        Assert.True(startResponse.Response!.ShouldEndSession != false, "the keep-alive ack shape must stay untouched (JF-410/JF-299)");

        var stopResponse = await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", token, offset: 3000), context, user, CreateSession(), CancellationToken.None);
        Assert.NotNull(stopResponse);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopReports) >= 2, TimeSpan.FromSeconds(2), 10);

        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == new Guid(token))),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PlaybackStarted_NormalOrdering_DoesNotReissueStop()
    {
        // JF-425: when the start report completes before the stop (normal ordering), both
        // reports stand exactly once; no corrective re-stop may be issued.
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        string token = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", token), context, user, CreateSession(), CancellationToken.None);
        await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", token, offset: 3000), context, user, CreateSession(), CancellationToken.None);

        _sessionManagerMock.Verify(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public async Task PlaybackStarted_DisplacementStop_DoesNotTriggerCorrection()
    {
        // JF-425/JF-447: a displacement stop (the old item's stop arriving AFTER a newer
        // play already started) must NOT supersede the in-flight start report: replaying
        // it would clobber the new track's now-playing entry. JF-447: the classification
        // reads the device's latest START, not the queue, so the test deliberately leaves
        // the device queue EMPTY (the PlaySong shape: several play paths never populate
        // it, which is what misclassified these stops before).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        Guid oldItem = Guid.NewGuid();
        Guid newItem = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == oldItem)))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == newItem)))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        // The OLD item's start report stalls; the NEW item's start completes.
        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", newItem.ToString()), context, user, CreateSession(), CancellationToken.None);

        // The old item's stop now arrives with a newer start on record: displacement.
        await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", oldItem.ToString(), offset: 100), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        // The displacement stop cleared the new track's session entry server-side, so the
        // handler restores it with an automated progress report (JF-447).
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == newItem), true),
            Times.Once);

        startGate.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_NewerStartAfterStop_DoesNotReissueStop()
    {
        // JF-425: when a NEW start follows the stop, that start owns the session; the late
        // report for the older start must not replay the stop over it. JF-447: the stale
        // report instead RESTORES the newer start's entry with an automated progress
        // report (its own write may have landed after the newer start's write).
        Guid itemA = Guid.NewGuid();
        Guid itemB = Guid.NewGuid();
        var startGateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemA)))
            .Returns(startGateA.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemB)))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA.ToString()), context, user, CreateSession(), CancellationToken.None);
        await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA.ToString(), offset: 3000), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemB.ToString()), context, user, CreateSession(), CancellationToken.None);

        startGateA.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == itemB), true),
            Times.Once);
    }

    [Fact]
    public async Task PlaybackStarted_DisplacementFinish_DoesNotTriggerCorrection()
    {
        // JF-425/JF-447: same displacement classification as PlaybackStoppedEventHandler.
        // When a newer play already started, the old item's PlaybackFinished must NOT
        // supersede the in-flight start report: replaying it would clobber the new
        // track's now-playing entry. Queue deliberately left empty (JF-447: the
        // classification no longer reads queue state).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        Guid oldItem = Guid.NewGuid();
        Guid newItem = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == oldItem)))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == newItem)))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", newItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        await CreateFinishedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", oldItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == newItem), true),
            Times.Once);

        startGate.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_DisplacementFailure_DoesNotTriggerCorrection()
    {
        // JF-425/JF-447: same displacement classification as PlaybackStoppedEventHandler,
        // applied to PlaybackFailed. A failure for the OLD item while a newer play already
        // started must not be registered as a superseding stop. Queue deliberately left
        // empty (JF-447: the classification no longer reads queue state).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        Guid oldItem = Guid.NewGuid();
        Guid newItem = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == oldItem)))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == newItem)))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", newItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        await CreateFailedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed", oldItem.ToString()), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == newItem), true),
            Times.Once);

        startGate.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_FinishOfCurrentItem_ReissuesStopToClearZombie()
    {
        // JF-425 polarity guard: a Finished for the item the queue still expects is a real
        // end of playback and MUST register the superseding stop (the displacement guard
        // may only skip events whose token mismatches the queue's current item).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        string token = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", token), context, user, CreateSession(), CancellationToken.None);
        await CreateFinishedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", token), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopReports) >= 2, TimeSpan.FromSeconds(2), 10);

        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == new Guid(token))),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PlaybackFailed_SleepTimerCompositeToken_CompletesAndRecordsStop()
    {
        // SleepTimerIntentHandler mints composite tokens ("{guid}|sleep:{ticks}"): the old
        // new Guid(token) threw FormatException, aborting the handler before the ordering
        // registration and before the keep-alive ack Amazon requires. The handler must
        // complete, report the stop for the embedded item ID, and register the stop so the
        // in-flight start report re-issues it.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        Guid itemId = Guid.NewGuid();
        string token = StreamTokenCodec.MintSleepTimerToken(itemId, 12345);
        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        // The Started for the sleep track carries the SAME composite token (as Amazon
        // echoes it), so the finish classifies as a real stop for the embedded item.
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", token), context, user, CreateSession(), CancellationToken.None);

        var response = await CreateFailedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed", token), context, user, CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response!.ShouldEndSession != false, "PlaybackFailed must return a keep-alive ack shape (JF-299)");
        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemId && i.Failed)),
            Times.Once);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopReports) >= 2, TimeSpan.FromSeconds(2), 10);

        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemId && i.Failed)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PlaybackStarted_StaleStartAfterNewerStart_RestoresNewerItem()
    {
        // JF-447 F3 start-vs-start: the older start's report is stalled while the newer
        // start's report completes; the slot was cleared by the newer BeginStart, so the
        // old code had NO correction and the stale write resurrected the OLD item as the
        // zombie. The generation check must instead restore the newer item's session
        // entry with an AUTOMATED progress report (never a start re-issue: Jellyfin
        // increments PlayCount per user inside OnPlaybackStart).
        Guid itemA = Guid.NewGuid();
        Guid itemB = Guid.NewGuid();
        var startGateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemA)))
            .Returns(startGateA.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemB)))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA.ToString()), context, user, CreateSession(), CancellationToken.None);
        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemB.ToString()), context, user, CreateSession(), CancellationToken.None);

        startGateA.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        // No stop existed anywhere: the only corrective action is restoring item B.
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Never);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == itemB), true),
            Times.Once);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == itemA), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaybackStarted_CorrectionRevalidates_AfterItsOwnStopLands()
    {
        // JF-447 F6 correction re-validation: the corrective OnPlaybackStopped is itself
        // in flight while a NEWER start (B) completes and writes its session entry; the
        // stale correction's write then clears it. The correction must re-check after
        // its own await and restore the newer start.
        Guid itemA = Guid.NewGuid();
        Guid itemB = Guid.NewGuid();
        var startGateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var correctionStopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCalls = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemA)))
            .Returns(startGateA.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.Is<PlaybackStartInfo>(i => i.ItemId == itemB)))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopCalls))
            .Returns(() => Volatile.Read(ref stopCalls) == 2 ? correctionStopGate.Task : Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA.ToString()), context, user, CreateSession(), CancellationToken.None);
        await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA.ToString(), offset: 3000), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopCalls));

        // The stale start report completes and fires the (gated) corrective stop.
        startGateA.TrySetResult();
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopCalls) >= 2, TimeSpan.FromSeconds(2), 10);

        // The newer start begins and completes while the correction is in flight.
        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemB.ToString()), context, user, CreateSession(), CancellationToken.None);

        // Release the corrective stop: its write landed after B's write, so B is restored.
        correctionStopGate.TrySetResult();
        await WaitForStartReportsToSettleAsync();

        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Exactly(2));
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == itemB), true),
            Times.Once);
    }

    [Fact]
    public async Task PlaybackStopped_Displacement_ConcurrentStopReport_CorrectionWaitsForOriginal()
    {
        // JF-447 F5 double-stop: the recorded stop's own OnPlaybackStopped is still in
        // flight when the superseded start report completes. The correction must WAIT
        // for the original instead of firing a concurrent duplicate (duplicate
        // SaveUserData transactions and activity entries): while the original is in
        // flight there is exactly one stop call, and the second lands only after the
        // first settles, sequentially.
        Guid itemA = Guid.NewGuid();
        var startGateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalStopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCalls = 0;
        bool secondStopOverlappedFirst = false;
        Task firstStopTask = Task.CompletedTask;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGateA.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopCalls))
            .Returns(() =>
            {
                if (Volatile.Read(ref stopCalls) == 1)
                {
                    firstStopTask = originalStopGate.Task;
                    return firstStopTask;
                }

                // Second (corrective) call: overlap is exactly the F5 harm; it must
                // never observe the original's task still running.
                secondStopOverlappedFirst = !firstStopTask.IsCompleted;
                return Task.CompletedTask;
            });

        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA.ToString()), context, user, CreateSession(), CancellationToken.None);

        // The stop handler's own report is dispatched but gated (never settles yet);
        // HandleAsync is still awaiting it.
        var stopTask = CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA.ToString(), offset: 3000), context, user, CreateSession(), CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopCalls) >= 1, TimeSpan.FromSeconds(2), 10);

        // The superseded start report completes while the original stop is in flight.
        startGateA.TrySetResult();

        // Deterministic observation point: the correction is PARKED waiting on the
        // original stop's completion.
        Assert.True(
            await TestHelpers.WaitUntilAsync(() => PlaybackReportOrdering.AnyCorrectionsWaitingOnInFlightStops, TimeSpan.FromSeconds(2), 10),
            "the correction must park waiting for the in-flight original stop");
        Assert.Equal(1, Volatile.Read(ref stopCalls));

        // The original settles; the correction then re-issues the stop, sequentially.
        originalStopGate.TrySetResult();
        await stopTask;
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref stopCalls) >= 2, TimeSpan.FromSeconds(2), 10);
        await WaitForStartReportsToSettleAsync();

        Assert.Equal(2, Volatile.Read(ref stopCalls));
        Assert.False(secondStopOverlappedFirst, "the corrective stop must never run concurrently with the original");
    }

    [Fact]
    public async Task PlaybackStopped_QueueNeverPopulated_NewerStartClassifiesDisplacement()
    {
        // JF-447 F2 displacement-state trust: 'play album A1' then 'play song S' on a
        // play path that never populates the device queue (PlaySongIntentHandler never
        // SetQueue). The queue-based classifier saw CurrentItemId=A1 and treated A1's
        // displacement stop as a REAL stop; the ordering-based classifier sees the
        // device's latest start (S) and classifies the displacement correctly, with the
        // queue left completely empty.
        Guid itemA1 = Guid.NewGuid();
        Guid itemS = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        var queue = _queueManager.GetOrCreateQueue(context.System!.Device!.DeviceID);
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA1.ToString()), context, user, CreateSession(), CancellationToken.None);
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemS.ToString()), context, user, CreateSession(), CancellationToken.None);

        var response = await CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA1.ToString(), offset: 42000), context, user, CreateSession(), CancellationToken.None);

        // Displacement: keep-alive (not session-ending), one stop report for the old
        // item with offset 0 (the real position of A1 must not be overwritten), and the
        // new track's session entry restored after the stop's write cleared it.
        Assert.True(response.Response!.ShouldEndSession != true, "a displacement stop keeps the session alive for the new track");
        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemA1 && i.PositionTicks == 0)),
            Times.Once);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(i => i.ItemId == itemS), true),
            Times.Once);

        // The queue stays empty and the displacement wrote no resume position for A1.
        Assert.Empty(queue.ItemIds);
        Assert.Null(queue.CurrentItemId);

        // And no correction can ever re-issue the displacement stop over item S.
        await WaitForStartReportsToSettleAsync();
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public async Task PlaybackStopped_BeforeNewStartProcessed_QueueNotYetAdvanced_SavesNearZeroPosition()
    {
        // JF-447 review finding 4 (race window, CURRENT behavior pinned): a
        // displacement stop can be PROCESSED before the new item's PlaybackStarted.
        // At stop time the ordering classifier still sees the OLD start
        // (LastStart=A1), and the queue pointer has not advanced either (the play
        // path that issued the new directive never touched this queue), so BOTH
        // signals name A1 and the near-zero displacement offset overwrites A1's
        // saved position (the 4ab4704b class). This test pins the current behavior
        // on purpose: any change to the classification semantics must fail here and
        // force an explicit design decision (see the known-open-window paragraph in
        // PlaybackReportOrdering's class doc).
        Guid itemA1 = Guid.NewGuid();
        Guid itemX1 = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        string device = context.System!.Device!.DeviceID;
        var queue = _queueManager.GetOrCreateQueue(device);
        queue.ItemIds = new List<string> { itemA1.ToString(), itemX1.ToString() };
        queue.CurrentItemId = itemA1.ToString(); // queue NOT yet advanced: still the old item
        var user = TestHelpers.CreateTestUser();

        // A1 started earlier, so the classifier's latest-start snapshot names A1.
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA1.ToString()), context, user, CreateSession(), CancellationToken.None);
        await WaitForStartReportsToSettleAsync();

        // The displacement stop for A1 arrives BEFORE Started(X1) is processed,
        // carrying the near-zero offset Alexa reports for the displaced old stream.
        var response = await CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA1.ToString(), offset: 200), context, user, CreateSession(), CancellationToken.None);

        // Current behavior: classified REAL (session ends, report carries the offset)...
        long nearZeroTicks = TimeSpan.FromMilliseconds(200).Ticks;
        Assert.True(response.Response!.ShouldEndSession == true, "a stop the classifier sees as real ends the session");
        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemA1 && i.PositionTicks == nearZeroTicks)),
            Times.Once);

        // ...and the near-zero offset DID overwrite A1's saved position in the plugin
        // stores (the pinned defect).
        Assert.Equal(itemA1.ToString(), queue.CurrentItemId);
        Assert.Equal(TimeSpan.FromMilliseconds(200).Ticks, queue.CurrentPositionTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(200).Ticks, queue.ItemPositionState[itemA1.ToString("N")]);

        // Only after the new start processes does the classifier catch up: from here
        // on, a later stop for A1 would classify as displacement.
        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemX1.ToString()), context, user, CreateSession(), CancellationToken.None);
        await WaitForStartReportsToSettleAsync();
        Assert.True(PlaybackReportOrdering.IsDisplacementStop(device, itemA1.ToString()));
    }

    [Fact]
    public async Task PlaybackStopped_BeforeNewStartProcessed_QueueAlreadyAdvanced_SkipsPositionOverwrite()
    {
        // JF-447 review finding 4 hardening: same inverted order, but the maintained
        // queue ALREADY advanced at directive time (the play path set
        // CurrentItemId=X1 when it issued the new AudioPlayer.Play, strictly before
        // the old stream's stop can arrive). The queue pointer is directive-time
        // truth: when it contradicts the event token while the stopped item is still
        // queued, the near-zero offset is suspect and the position overwrite is
        // skipped, even though the start-based classification still says REAL (the
        // stop report itself goes out unchanged).
        Guid itemA1 = Guid.NewGuid();
        Guid itemX1 = Guid.NewGuid();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(Task.CompletedTask);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        var context = CreateContextForFreshDevice();
        string device = context.System!.Device!.DeviceID;
        var queue = _queueManager.GetOrCreateQueue(device);
        queue.ItemIds = new List<string> { itemA1.ToString(), itemX1.ToString() };
        queue.CurrentItemId = itemX1.ToString(); // directive-time pointer: the NEW item
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(3).Ticks;
        long a1SavedProgress = TimeSpan.FromMinutes(2).Ticks;
        queue.ItemPositionState[itemA1.ToString("N")] = a1SavedProgress;
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", itemA1.ToString()), context, user, CreateSession(), CancellationToken.None);
        await WaitForStartReportsToSettleAsync();

        var response = await CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", itemA1.ToString(), offset: 200), context, user, CreateSession(), CancellationToken.None);

        // Classification semantics are unchanged: the stop report still goes out
        // exactly once with the event's offset, and the session still ends.
        Assert.True(response.Response!.ShouldEndSession == true);
        long nearZeroTicks = TimeSpan.FromMilliseconds(200).Ticks;
        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemA1 && i.PositionTicks == nearZeroTicks)),
            Times.Once);

        // But the position overwrite is SKIPPED: A1's saved progress survives, the
        // pointer does not move back to the old item, and no queue/UserData write
        // lands for the suspect near-zero offset.
        Assert.Equal(itemX1.ToString(), queue.CurrentItemId);
        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, queue.CurrentPositionTicks);
        Assert.Equal(a1SavedProgress, queue.ItemPositionState[itemA1.ToString("N")]);
    }

    /// <summary>
    /// Waits until every fire-and-forget start report (correction included) has settled
    /// (JF-447 completion seam). This is the deterministic replacement for the old
    /// sleep-based absence checks: once no report is in flight, its corrective calls
    /// have either happened or never will.
    /// </summary>
    private static async Task WaitForStartReportsToSettleAsync()
        => await TestHelpers.WaitUntilAsync(
            () => !PlaybackReportOrdering.AnyStartReportsInFlight,
            TimeSpan.FromSeconds(2),
            pollMs: 10);

    [Fact]
    public void PlaybackFinished_CanHandle_ReturnsTrueForPlaybackFinished()
    {
        var handler = CreateFinishedHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackFinished_Handle_ReturnsEmptyResponse()
    {
        var handler = CreateFinishedHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", offset: 10000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    // JF-683 (live 2026-09-30, three occurrences in one Norah Jones round): the
    // PlaybackFinished request arrives with playerActivity=FINISHED during the ~1s
    // gap before the ALREADY-ENQUEUED next stream starts. The activity-only gate
    // read that gap as queue exhaustion and ended the session (dismissing the APL
    // screen) at every track boundary; the user's later pause arrived
    // sessionNew=true because of it. When the finished item still has a successor
    // in the session queue, a next stream exists and the session must survive the
    // gap.
    [Fact]
    public async Task PlaybackFinished_InterTrackGapWithQueuedSuccessor_KeepsSessionAlive()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreateFinishedHandler(LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(TestCaptureLogger.Into(records));
        }));
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        Guid track3 = Guid.NewGuid();
        var session = CreateSession();
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 2 ? track3 : Guid.NewGuid() })
            .ToList();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = track3, Name = "Track 3" };

        // The inter-track race window: the live-verified playerActivity value, on a
        // fresh device so the ordering/displacement state is this test's own.
        var context = TestHelpers.CreateContextWithToken(track3.ToString(), $"jf683-{Guid.NewGuid():N}", "FINISHED");

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", track3.ToString()),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession != true, "a finished track with a queued successor must not end the session: the inter-track activity gap is not exhaustion");
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("queue exhausted", StringComparison.Ordinal));
        Assert.Contains(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("index=2 of 5", StringComparison.Ordinal)
                && r.Message.Contains("keeping the session alive", StringComparison.Ordinal));
    }

    // JF-683 companion: the queue-successor arm must not over-correct. At TRUE
    // exhaustion (the finished item is the last queue entry, activity FINISHED,
    // nothing enqueued) the session still ends so the APL screen dismisses, and the
    // exhausted log names its view (queue count + resolved index + activity).
    [Fact]
    public async Task PlaybackFinished_LastItemNoSuccessor_EndsSessionAndLogsView()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreateFinishedHandler(LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(records))));
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        Guid lastTrack = Guid.NewGuid();
        var session = CreateSession();
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 4 ? lastTrack : Guid.NewGuid() })
            .ToList();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = lastTrack, Name = "Track 5" };

        var context = TestHelpers.CreateContextWithToken(lastTrack.ToString(), $"jf683-{Guid.NewGuid():N}", "FINISHED");

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", lastTrack.ToString()),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession == true, "true exhaustion (no successor, not actively playing) still ends the session");
        string? exhaustedLine = TestCaptureLogger.Snapshot(records)
            .FirstOrDefault(r => r.Message.Contains("queue exhausted", StringComparison.Ordinal)).Message;
        Assert.NotNull(exhaustedLine);
        Assert.Contains("queueCount=5", exhaustedLine, StringComparison.Ordinal);
        Assert.Contains("finishedIndex=4", exhaustedLine, StringComparison.Ordinal);
        Assert.Contains("playerActivity=FINISHED", exhaustedLine, StringComparison.Ordinal);
    }

    // JF-683 rework (review F3/F4/F5): the sleep-expiry carve-out and the loop-mode
    // arm are decision branches with their own pins. The carve-out mirrors
    // NearlyFinished's expired-deadline gate through the ONE shared predicate
    // (StreamTokenCodec.IsSleepExpiredUtc); these pins are its parity tests.
    private static Context CreateFinishedContextWithSleepToken(string itemId, long deadlineUtcTicks, string deviceId)
        => TestHelpers.CreateContextWithToken(
            FormattableString.Invariant($"{itemId}|sleep:{deadlineUtcTicks}"), deviceId, "FINISHED");

    [Fact]
    public async Task PlaybackFinished_ExpiredSleepToken_AtBoundary_EndsSessionAndLogsCarveOut()
    {
        // The sleep timer's stop-and-dismiss intent: at an expired deadline
        // NearlyFinished enqueued NOTHING, so despite a positional successor the
        // session ends. The carve-out line names the reason (the exhausted line
        // alone would report finishedIndex=-1 on a populated queue).
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreateFinishedHandler(LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(TestCaptureLogger.Into(records));
        }));
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        Guid track3 = Guid.NewGuid();
        var session = CreateSession();
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 2 ? track3 : Guid.NewGuid() })
            .ToList();

        long pastDeadline = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        var context = CreateFinishedContextWithSleepToken(track3.ToString(), pastDeadline, $"jf683-{Guid.NewGuid():N}");

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", $"{track3}|sleep:{pastDeadline}"),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession == true, "an expired sleep timer ends the session even with a queued successor: playback stops by design");
        Assert.Contains(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("sleep timer expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlaybackFinished_FutureSleepToken_AtBoundary_KeepsSessionAlive()
    {
        // Parity half: a sleep timer that has NOT expired yet must not suppress the
        // successor arm (an inverted comparison would end the session mid-playback).
        var handler = CreateFinishedHandler();
        Guid track3 = Guid.NewGuid();
        var session = CreateSession();
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 2 ? track3 : Guid.NewGuid() })
            .ToList();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        long futureDeadline = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
        var context = CreateFinishedContextWithSleepToken(track3.ToString(), futureDeadline, $"jf683-{Guid.NewGuid():N}");

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", $"{track3}|sleep:{futureDeadline}"),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession != true, "a future sleep deadline must not suppress the queued-successor keep-alive");
    }

    [Theory]
    [InlineData(RepeatMode.RepeatOne)]
    [InlineData(RepeatMode.RepeatAll)]
    public async Task PlaybackFinished_LoopModeAtLastPosition_KeepsSessionAlive(RepeatMode loopMode)
    {
        // The loops arm (review F3): RepeatOne replays the same item and RepeatAll
        // wraps to index 0, so at the LAST queue position a next stream IS enqueued
        // while no positional successor exists. Loop mode counts as queued-next.
        Guid lastTrack = Guid.NewGuid();
        var session = CreateSession();
        session.PlayState = new PlayerStateInfo { RepeatMode = loopMode };
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 4 ? lastTrack : Guid.NewGuid() })
            .ToList();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        var context = TestHelpers.CreateContextWithToken(lastTrack.ToString(), $"jf683-{Guid.NewGuid():N}", "FINISHED");

        var response = await CreateFinishedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", lastTrack.ToString()),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession != true, "loop mode keeps playback going at the last position: the session must survive the inter-track gap");
    }

    // ---- JF-691: the two residuals of the JF-683 keep-alive ----

    /// <summary>
    /// The JF-691 chain drives NearlyFinished's real enqueue path, whose stream URL
    /// builder needs a server address (the GaplessPlaybackTests precedent; the
    /// class-level _config stays bare for the event-only tests).
    /// </summary>
    private static readonly PluginConfiguration Jf691Config = new() { ServerAddress = "http://localhost:8096" };

    /// <summary>
    /// The NearlyFinished half of the JF-691 chain must be constructed on THIS
    /// class's manager and the SAME config instance as its Finished counterpart so
    /// the enqueue record both write and read lands in one store (the file's
    /// manager-less helpers deliberately omit the manager for the heuristic pins).
    /// </summary>
    private PlaybackNearlyFinishedEventHandler CreateNearlyFinishedHandlerOnClassManager()
        => new(_sessionManagerMock.Object, Jf691Config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory, _queueManager);

    // JF-691 corner 1: shuffle requested WITHOUT a physical reshuffle makes
    // NearlyFinished's ResolveNextItemId random-pick a next track at EVERY
    // position with count > 1, including the last one, so playback genuinely
    // continues there. The JF-683 arm saw finishedIndex = Count-1 and RepeatNone
    // and ended the session at that boundary (the dismissed-screen, pause
    // sessionNew=true symptom persisted in this mode). The arm is the resolver's
    // own admission condition (Shuffle, NOT reshuffled, count > 1, index found),
    // so it cannot fire where the resolver would have answered null.
    [Fact]
    public async Task PlaybackFinished_ShuffleRandomAtLastPosition_KeepsSessionAlive()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreateFinishedHandler(LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(TestCaptureLogger.Into(records));
        }));
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        Guid lastTrack = Guid.NewGuid();
        var session = CreateSession();
        // No device queue: ResolvePlaybackOrder falls back to the session PlayState,
        // the same fallback NearlyFinished's resolver uses manager-less.
        session.PlayState = new PlayerStateInfo { PlaybackOrder = PlaybackOrder.Shuffle };
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 4 ? lastTrack : Guid.NewGuid() })
            .ToList();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = lastTrack, Name = "Track 5" };

        var context = TestHelpers.CreateContextWithToken(lastTrack.ToString(), $"jf691-{Guid.NewGuid():N}", "FINISHED");

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", lastTrack.ToString()),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession != true, "unreshuffled shuffle random-picks a next track at the last position: the session must survive the boundary");
        Assert.Contains(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("keeping the session alive", StringComparison.Ordinal)
                && r.Message.Contains("shuffleRandom=True", StringComparison.Ordinal));
    }

    // The honest negative of corner 1: a PHYSICALLY reshuffled queue (ShuffleOn's
    // OriginalItemIds marker) advances sequentially through its shuffled order, so
    // at the last position true exhaustion IS reachable and the session must end.
    // The enqueue record is armed first so the JF-691 veto passes and the ARMS
    // alone decide this pin.
    [Fact]
    public async Task PlaybackFinished_ShuffledReshuffledQueueAtLastPosition_EndsSession()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreateFinishedHandlerOnClassManager();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        Guid lastTrack = Guid.NewGuid();
        List<string> ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString())
            .Append(lastTrack.ToString()).ToList();

        // The production reshuffle shape: device queue marked Shuffle with the
        // pre-shuffle order kept (OriginalItemIds non-null), finished item last.
        _queueManager.SetQueue(deviceId, ids, currentIndex: 4);
        _queueManager.GetOrCreateQueue(deviceId).OriginalItemIds = new List<string>(ids);
        _queueManager.RecordEnqueue(deviceId, lastTrack.ToString(), Guid.NewGuid().ToString());

        var session = CreateSession();
        session.NowPlayingQueue = ids.Select(id => new QueueItem { Id = Guid.Parse(id) }).ToList();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = lastTrack, Name = "Last" };

        var response = await handler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", lastTrack.ToString()),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession == true, "a reshuffled queue advances sequentially: the last position is true exhaustion and ends the session");
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("keeping the session alive", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two-track scene both JF-691 chain pins start from: the device store
    /// carries the queue the play seeded (the production play-flow mirror), the
    /// session queue matches it, track1 is now-playing and resolvable, and the
    /// Finished handler runs on the class manager with the server-address config
    /// and a capture logger. The caller decides the successor's library fate
    /// (present or deleted) and drives the two event steps itself.
    /// </summary>
    private (SessionInfo Session, string DeviceId, Guid Track1, Guid Track2, PlaybackFinishedEventHandler Finished) SeedTwoTrackJf691Scene(List<(LogLevel Level, string Message)> records)
    {
        string deviceId = $"jf691-{Guid.NewGuid():N}";
        Guid track1 = Guid.NewGuid();
        Guid track2 = Guid.NewGuid();

        _queueManager.SetQueue(deviceId, new List<string> { track1.ToString(), track2.ToString() }, currentIndex: 0);

        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1 },
            new() { Id = track2 }
        };
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = track1, Name = "Track 1" };

        _libraryManagerMock.Setup(l => l.GetItemById(track1)).Returns(session.FullNowPlayingItem);

        var finished = CreateFinishedHandlerOnClassManager(Jf691Config, LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(TestCaptureLogger.Into(records));
        }));

        return (session, deviceId, track1, track2, finished);
    }

    // JF-691 corner 2, the end-to-end deleted-successor chain on the class manager:
    // NearlyFinished resolves the positional successor the library can no longer
    // serve (deleted from the library) and returns Empty WITHOUT enqueueing, so
    // the enqueue record stays untouched; the device then stops after the current
    // stream, yet the JF-683 positional arm kept the session and its APL screen
    // alive over dead audio until Amazon reaped it. The record veto closes it:
    // nothing was enqueued for this boundary, so the session ends.
    [Fact]
    public async Task PlaybackFinished_DeletedSuccessor_NothingEnqueued_EndsSession()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var (session, deviceId, track1, track2, finished) = SeedTwoTrackJf691Scene(records);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        // The successor is gone from the library.
        _libraryManagerMock.Setup(l => l.GetItemById(track2)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        SkillResponse nearlyFinished = await CreateNearlyFinishedHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackNearlyFinished", track1.ToString()),
            TestHelpers.CreateContextWithToken(track1.ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The precondition the whole corner rests on: NOTHING was enqueued, and the
        // record (the enqueue the device was actually told about) stayed untouched.
        Assert.Empty(nearlyFinished.Response!.Directives ?? new List<IDirective>());
        Assert.Null(_queueManager.GetQueue(deviceId)!.LastEnqueueAfterToken);

        var response = await finished.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", track1.ToString()),
            TestHelpers.CreateContextWithToken(track1.ToString(), deviceId, "FINISHED"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession == true, "nothing was enqueued for this boundary: the session must end instead of lingering over dead audio");
        // The ending reason must be visible at the PRODUCTION log level (the
        // JF-683 review-F4 lesson): the dedicated Info line, not only the Debug
        // record view and never the misleading "queue exhausted" line.
        Assert.Contains(
            TestCaptureLogger.Snapshot(records),
            r => r.Level == LogLevel.Information
                && r.Message.Contains("nothing was enqueued for this boundary", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(records),
            r => r.Message.Contains("queue exhausted", StringComparison.Ordinal));
    }

    // The healthy companion: when NearlyFinished DID enqueue (the record names this
    // boundary's token), the veto passes and the JF-683 inter-track keep-alive
    // works on a manager-backed session too - the veto must not over-fire on the
    // boundary it exists to protect.
    [Fact]
    public async Task PlaybackFinished_EnqueueRecordMatches_KeepsSessionAlive()
    {
        var (session, deviceId, track1, track2, finished) = SeedTwoTrackJf691Scene(new List<(LogLevel Level, string Message)>());
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        _libraryManagerMock.Setup(l => l.GetItemById(track2))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = track2, Name = "Track 2" });

        await CreateNearlyFinishedHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackNearlyFinished", track1.ToString()),
            TestHelpers.CreateContextWithToken(track1.ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal(track1.ToString(), _queueManager.GetQueue(deviceId)!.LastEnqueueAfterToken);

        var response = await finished.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", track1.ToString()),
            TestHelpers.CreateContextWithToken(track1.ToString(), deviceId, "FINISHED"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession != true, "the record names this boundary's enqueue: the session survives the inter-track gap");
    }

    [Fact]
    public void PlaybackStopped_CanHandle_ReturnsTrueForPlaybackStopped()
    {
        var handler = CreateStopHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackStopped_Handle_ReturnsEmptyResponse()
    {
        var handler = CreateStopHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", offset: 3000);

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Once);
    }

    [Fact]
    public void PlaybackFailed_CanHandle_ReturnsTrueForPlaybackFailed()
    {
        var handler = CreateFailedHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackFailed_Handle_ReturnsEmptyResponse()
    {
        var handler = CreateFailedHandler();
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed");

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        _sessionManagerMock.Verify(s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.Failed)), Times.Once);
    }

    [Fact]
    public void SessionEnded_CanHandle_ReturnsTrueForSessionEndedRequest()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(new SessionEndedRequest()));
    }

    [Fact]
    public void SessionEnded_CanHandle_ReturnsFalseForIntentRequest()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.False(handler.CanHandle(new IntentRequest()));
    }

    [Fact]
    public async Task SessionEnded_Handle_ReturnsEmpty()
    {
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new SessionEndedRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public void ExceptionHandler_CanHandle_ReturnsTrueForSystemExceptionRequest()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(new SystemExceptionRequest()));
    }

    [Fact]
    public void ExceptionHandler_CanHandle_ReturnsFalseForIntentRequest()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.False(handler.CanHandle(new IntentRequest()));
    }

    /// <summary>
    /// JF-507: System.ExceptionEncountered may not carry outputSpeech ("Your skill
    /// can't return a response"); the previous Tell was itself an INVALID_RESPONSE
    /// (live incident 2026-09-06 17:12:54 corr=e54b0532 answered "Qualcosa è andato
    /// storto"). The handler still classifies and logs, but answers keep-alive.
    /// </summary>
    [Fact]
    public async Task ExceptionHandler_Handle_ReturnsKeepAliveWithoutOutputSpeech()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new SystemExceptionRequest { Error = new Error { Message = "test error" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives);
    }

    // ========== JF-507: degradation responses on event requests ==========

    /// <summary>
    /// Shared JF-507 harness: a Plugin.Instance (HandleRequestAsync needs it), a config
    /// with one user, and a session manager whose lookup MISSES (the JF-477 fast-fail
    /// degradation the incident exercised). The context carries the user's ID as the
    /// access token so the user resolves and the session lookup is the failing step.
    /// </summary>
    private (PluginConfiguration Config, Entities.User User, Mock<ISessionManager> SessionManager, Context Context) CreateSessionMissHarness()
    {
        var tmpDir = TestHelpers.CreateRegisteredTempDir("jf507-event-shape");
        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        var userManager = new Mock<IUserManager>();
        _ = new Plugin(appPaths.Object, xmlSerializer.Object, _loggerFactory, userManager.Object);

        var config = new PluginConfiguration();
        var user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "jf507-token" };
        config.AddUser(user);
        TestHelpers.SetServerAddress(config, "https://test.example.com");

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionInfo?)null);

        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
                Device = new Device { DeviceID = $"jf507-{Guid.NewGuid():N}" }
            }
        };

        return (config, user, sessionManager, context);
    }

    /// <summary>
    /// The incident shape (2026-09-06 17:12:52, requestId 970b119f): a PlaybackFailed
    /// event whose session lookup missed (the JF-477 fast-fail degradation) must answer
    /// with the empty keep-alive, NOT the UserNotFound Tell: Amazon rejects outputSpeech
    /// on AudioPlayer event responses with INVALID_RESPONSE "Response may not contain
    /// an outputSpeech".
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_PlaybackFailed_SessionNotFound_ReturnsKeepAlive()
    {
        var (config, _, sessionManager, context) = CreateSessionMissHarness();
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackFailed",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 1
        };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives);
    }

    /// <summary>
    /// The degradation keeps its UserNotFound Tell for NON-event requests (an intent
    /// whose session lookup missed still speaks the re-link prompt).
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_SessionNotFound_KeepsUserNotFoundTell()
    {
        var (config, _, sessionManager, context) = CreateSessionMissHarness();
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
    }

    // ========== JF-527: dead-token discrimination on the session-miss path ==========

    /// <summary>
    /// Shared JF-527 seam: attach a DeviceQueueManager to the harness plugin instance
    /// (the property's internal setter is the InternalsVisibleTo seam) and record a
    /// previous play for the harness device, the evidence that the user's JellyfinToken
    /// used to work before the server update killed it.
    /// </summary>
    private void RecordPreviousPlayOnHarnessDevice(Context context)
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("jf527");
        Plugin.Instance!.DeviceQueueManager = queueManager;
        queueManager.RecordLastPlayed(context.System.Device.DeviceID, Guid.NewGuid().ToString(), DeviceQueueManager.LaunchRoute.Audio);

        // Disposal tears down the armed 2s debounce timer deterministically (no
        // post-test straggler); the in-memory queue (what the handler's
        // GetLastPlayedItemId read hits) survives Dispose.
        queueManager.Dispose();
    }

    /// <summary>
    /// The dead-token shape (2026-09-08 12.0 incident): non-empty JellyfinToken plus a
    /// recorded previous play on the device. The miss must speak the actionable
    /// AccountRelinkRequired tell and attach a card carrying the same message.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_SessionNotFound_DeadTokenWithPlayHistory_SpeaksRelinkTellWithCard()
    {
        var (config, _, sessionManager, context) = CreateSessionMissHarness();
        RecordPreviousPlayOnHarnessDevice(context);
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        string expected = ResponseStrings.Get("AccountRelinkRequired", "en-US");
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(expected, speech.Text);
        var card = Assert.IsType<StandardCard>(response.Response.Card);
        Assert.Equal(ResponseStrings.Get("AccountRelinkCardTitle", "en-US"), card.Title);
        Assert.Equal(expected, card.Content);
    }

    /// <summary>
    /// An empty token means the account was never linked: keep the generic
    /// UserNotFound tell, no card.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_SessionNotFound_EmptyToken_KeepsUserNotFoundTell()
    {
        var (config, user, sessionManager, context) = CreateSessionMissHarness();
        RecordPreviousPlayOnHarnessDevice(context);
        user.JellyfinToken = null;
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("UserNotFound", "en-US"), speech.Text);
        Assert.Null(response.Response.Card);
    }

    /// <summary>
    /// A token with NO play history on the device is not evidenced as previously
    /// working: keep the generic UserNotFound tell, no card.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_SessionNotFound_TokenWithoutPlayHistory_KeepsUserNotFoundTell()
    {
        var (config, _, sessionManager, context) = CreateSessionMissHarness();
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("UserNotFound", "en-US"), speech.Text);
        Assert.Null(response.Response.Card);
    }

    /// <summary>
    /// Event requests keep the JF-507 keep-alive shape even in the dead-token shape:
    /// outputSpeech on an AudioPlayer event response is an INVALID_RESPONSE, so the
    /// relink tell must not ride on events regardless of the evidence.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_EventRequest_SessionNotFound_DeadToken_KeepsKeepAlive()
    {
        var (config, _, sessionManager, context) = CreateSessionMissHarness();
        RecordPreviousPlayOnHarnessDevice(context);
        var handler = new PlaybackFailedEventHandler(sessionManager.Object, config, _loggerFactory);
        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackFailed",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 1
        };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Card);
        Assert.Empty(response.Response.Directives);
    }

    // ========== JF-588: self-heal the session-miss miss by re-registering the session ==========

    /// <summary>
    /// Probe handler whose HandleAsync output is unambiguous: it only runs when the
    /// request path got past the session resolution, so the JF-588 self-heal test can
    /// tell "the retry landed and the request was served" from the degradation tells.
    /// </summary>
    private sealed class SelfHealProbeHandler : BaseHandler
    {
        public SelfHealProbeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(
            Request request,
            Context context,
            Jellyfin.Plugin.AlexaSkill.Entities.User user,
            SessionInfo session,
            CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("self-heal-probe"));
    }

    /// <summary>
    /// JF-588 incident shape (2026-09-18: a container auto-update left the server unable
    /// to resolve the alive, DB-verified token session for hours): the FIRST session
    /// lookup misses, but the user has a JellyfinToken and the device has recorded play
    /// history. The plugin retries the lookup once (the review-corrected JF-588
    /// mechanism: LogSessionActivity cannot repair the Devices-row precondition, so
    /// the valuable part is the retry); the retry lands and the request is SERVED,
    /// not answered with the AccountRelinkRequired relink tell.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_FirstLookupMisses_RetryLands_ServesRequest()
    {
        var (config, user, sessionManager, context) = CreateSessionMissHarness();
        RecordPreviousPlayOnHarnessDevice(context);
        var session = TestHelpers.CreateTestSession(sessionManager.Object, _loggerFactory);
        sessionManager.SetupSequence(
                s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionInfo?)null)
            .ReturnsAsync(session);
        Mock.Get(Plugin.Instance!.UserManager)
            .Setup(u => u.GetUserById(user.Id))
            .Returns(TestHelpers.CreateJellyfinUser(id: user.Id));

        var handler = new SelfHealProbeHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("self-heal-probe", speech.Text);
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// The self-heal is cheap on every other miss path: without the token+history
    /// evidence (here: no token) LogSessionActivity never fires and only ONE lookup
    /// runs before the existing UserNotFound tell.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequest_SessionNotFound_EmptyToken_NeverReRegisters()
    {
        var (config, user, sessionManager, context) = CreateSessionMissHarness();
        RecordPreviousPlayOnHarnessDevice(context);
        user.JellyfinToken = null;
        var handler = new SelfHealProbeHandler(sessionManager.Object, config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "WhoAmIIntent" } };

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("UserNotFound", "en-US"), speech.Text);
        sessionManager.Verify(
            s => s.LogSessionActivity(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>()),
            Times.Never);
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// The predicate behind the degradation: every AudioPlayer event, SessionEnded, and
    /// SystemExceptionEncountered is event-shaped; intents and launch requests are not.
    /// </summary>
    [Fact]
    public void IsEventRequest_ClassifiesEventAndNonEventRequests()
    {
        Assert.True(BaseHandler.IsEventRequest(CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed")));
        Assert.True(BaseHandler.IsEventRequest(CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped")));
        Assert.True(BaseHandler.IsEventRequest(new SessionEndedRequest()));
        Assert.True(BaseHandler.IsEventRequest(new SystemExceptionRequest()));

        Assert.False(BaseHandler.IsEventRequest(new IntentRequest()));
        Assert.False(BaseHandler.IsEventRequest(new LaunchRequest()));
    }

    /// <summary>
    /// JF-507, interceptor level: an OPEN circuit must not short-circuit an AudioPlayer
    /// event with the ServerUnavailable Tell (outputSpeech on an event response is an
    /// INVALID_RESPONSE); the event passes through so its handler answers keep-alive.
    /// An intent request is still short-circuited.
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_Open_PassesEventRequestsThrough_AndStillShortCircuitsIntents()
    {
        var breaker = new Jellyfin.Plugin.AlexaSkill.Alexa.CircuitBreaker();
        TestHelpers.SetServerAddress(_config, $"https://{Guid.NewGuid():N}.example.com");
        // The ServerAddress setter normalizes a trailing slash onto the value, so the
        // breaker must key on the CONFIG's normalized form (the interceptor reads that).
        string serverUrl = _config.ServerAddress;
        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure(serverUrl, _loggerFactory.CreateLogger<Jellyfin.Plugin.AlexaSkill.Alexa.CircuitBreaker>());
        }

        Assert.False(breaker.IsRequestAllowed(serverUrl), "expected the circuit to be OPEN");

        var interceptor = new Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.CircuitBreakerInterceptor(
            breaker, _config, _loggerFactory.CreateLogger<Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.CircuitBreakerInterceptor>());

        var handler = CreateFailedHandler();
        var eventContext = new Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.RequestContext(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed"), CreateContext(), null, handler);
        bool eventContinues = await interceptor.ProcessAsync(eventContext, CancellationToken.None);
        Assert.True(eventContinues);
        Assert.Null(eventContext.Response);

        var intentContext = new Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.RequestContext(
            new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } }, CreateContext(), null, handler);
        bool intentContinues = await interceptor.ProcessAsync(intentContext, CancellationToken.None);
        Assert.False(intentContinues);
        Assert.NotNull(intentContext.Response?.Response?.OutputSpeech);
    }

    // ---- JF-655: the per-device active-audio flag lifecycle ----

    /// <summary>
    /// The event handlers that own the flag must be constructed on THIS class's
    /// manager so the assertions read the same store the events wrote (the file's
    /// Create*Handler helpers deliberately omit the manager for their own concerns).
    /// </summary>
    private PlaybackStartedEventHandler CreateStartHandlerOnClassManager()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _libraryManagerMock.Object, _queueManager);

    /// <summary>
    /// The class-manager Finished factory. The optional overrides exist for the
    /// JF-691 chain tests, which need a server-address config (their NearlyFinished
    /// counterpart builds real stream URLs) and a capture logger; the bare form
    /// keeps the pre-existing call sites unchanged.
    /// </summary>
    private PlaybackFinishedEventHandler CreateFinishedHandlerOnClassManager(PluginConfiguration? config = null, ILoggerFactory? loggerFactory = null)
        => new(_sessionManagerMock.Object, config ?? _config, loggerFactory ?? _loggerFactory, _queueManager, _libraryManagerMock.Object);

    private PlaybackFailedEventHandler CreateFailedHandlerOnClassManager()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager);

    /// <summary>
    /// The flag is a plugin-start-cleared signal: a FRESH manager on the SAME data
    /// directory (a boot) reloads the persisted stores (the ledger entry written
    /// before the restart survives) while the flag reads clear, so a restart can
    /// never re-launch stale audio off a persisted-looking active state (the flag
    /// never reaches the queue files at all). Review finding: the earlier form
    /// re-created the manager on a DIFFERENT directory, which could not detect the
    /// flag being persisted.
    /// </summary>
    [Fact]
    public void AudioActiveFlag_FreshManager_BootsClear()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        Guid item = Guid.NewGuid();
        string dir = TestHelpers.CreateRegisteredTempDir("jf655-boot");
        var logger = _loggerFactory.CreateLogger<DeviceQueueManager>();
        using (DeviceQueueManager first = new(dir, logger))
        {
            first.MarkAudioPlaybackStarted(deviceId);
            first.RecordLastPlayed(deviceId, item.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        } // Dispose flushes the debounced persist to disk.

        using DeviceQueueManager booted = new(dir, logger);
        Assert.Equal(item.ToString(), booted.GetLastPlayedItemId(deviceId));
        Assert.False(booted.IsAudioPlaybackActive(deviceId));
    }

    [Fact]
    public async Task AudioActiveFlag_PlaybackStartedEvent_MarksDeviceActive()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        Assert.False(_queueManager.IsAudioPlaybackActive(deviceId));

        await CreateStartHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", Guid.NewGuid().ToString()),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.True(_queueManager.IsAudioPlaybackActive(deviceId));
    }

    [Fact]
    public async Task AudioActiveFlag_PlaybackStoppedEvent_ClearsDeviceActive()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        string token = Guid.NewGuid().ToString();
        await CreateStartHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", token),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        await CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", token),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.False(_queueManager.IsAudioPlaybackActive(deviceId));
    }

    /// <summary>
    /// The code-review ordering pin: a displacement stop (the OLD stream's stop
    /// arriving after the NEW stream's PlaybackStarted was processed) must NOT
    /// clear the flag, or an actively-playing device would read inactive until the
    /// next start event.
    /// </summary>
    [Fact]
    public async Task AudioActiveFlag_DisplacedStopAfterNewStart_KeepsDeviceActive()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        string oldToken = Guid.NewGuid().ToString();
        string newToken = Guid.NewGuid().ToString();
        await CreateStartHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldToken),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        await CreateStartHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", newToken),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        // The displaced OLD stream's stop, delivered after the new start.
        await CreateStopHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", oldToken),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.True(_queueManager.IsAudioPlaybackActive(deviceId));
    }

    [Fact]
    public async Task AudioActiveFlag_PlaybackFinishedEvent_ClearsDeviceActive()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        _queueManager.MarkAudioPlaybackStarted(deviceId);

        await CreateFinishedHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", Guid.NewGuid().ToString()),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.False(_queueManager.IsAudioPlaybackActive(deviceId));
    }

    [Fact]
    public async Task AudioActiveFlag_PlaybackFailedEvent_ClearsDeviceActive()
    {
        Context context = CreateContextForFreshDevice();
        string deviceId = context.System.Device.DeviceID!;
        _queueManager.MarkAudioPlaybackStarted(deviceId);

        await CreateFailedHandlerOnClassManager().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed", Guid.NewGuid().ToString()),
            context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.False(_queueManager.IsAudioPlaybackActive(deviceId));
    }
}
