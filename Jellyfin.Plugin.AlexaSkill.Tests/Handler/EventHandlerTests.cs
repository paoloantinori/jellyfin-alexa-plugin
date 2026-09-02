using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
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
public class EventHandlerTests : PluginTestBase
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
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    /// <summary>
    /// Context with a fresh unique device ID. The JF-425 ordering tests must not share
    /// TestHelpers' fixed "test-device": DeviceQueueManager loads persisted queue files
    /// (Path.GetTempPath()/queue_*.json) across test runs, and leftover queue state for a
    /// shared device makes a stop for an unknown item classify as DISPLACEMENT, which
    /// skips the stop registration the tests exercise.
    /// </summary>
    private static Context CreateContextForFreshDevice() => TestHelpers.CreateTestContext($"jf425-{Guid.NewGuid():N}");

    private PlaybackStartedEventHandler CreateStartHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private PlaybackStoppedEventHandler CreateStopHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);

    private PlaybackFinishedEventHandler CreateFinishedHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager);

    private PlaybackFailedEventHandler CreateFailedHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager);

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
        // JF-425: a displacement stop (the old item's stop while the queue already points
        // at the new item) must NOT supersede the in-flight start report: replaying it
        // would clobber the new track's now-playing entry.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        string oldToken = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var queue = _queueManager.GetOrCreateQueue(context.System!.Device!.DeviceID);
        queue.ItemIds = new List<string> { Guid.NewGuid().ToString() };
        queue.CurrentIndex = 0;
        var user = TestHelpers.CreateTestUser();
        var startHandler = CreateStartHandler();
        var stopHandler = CreateStopHandler();

        await startHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldToken), context, user, CreateSession(), CancellationToken.None);
        await stopHandler.HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped", oldToken, offset: 100), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await GiveFireAndForgetContinuationsTimeToRunAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_NewerStartAfterStop_DoesNotReissueStop()
    {
        // JF-425: when a NEW start follows the stop, that start owns the session; the late
        // report for the older start must not replay the stop over it.
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
        await GiveFireAndForgetContinuationsTimeToRunAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_DisplacementFinish_DoesNotTriggerCorrection()
    {
        // JF-425: same displacement classification as PlaybackStoppedEventHandler. When the
        // queue already points at the NEW item, the old item's PlaybackFinished must NOT
        // supersede the in-flight start report: replaying it would clobber the new track's
        // now-playing entry.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        string oldToken = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var queue = _queueManager.GetOrCreateQueue(context.System!.Device!.DeviceID);
        queue.ItemIds = new List<string> { Guid.NewGuid().ToString() };
        queue.CurrentIndex = 0;
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldToken), context, user, CreateSession(), CancellationToken.None);
        await CreateFinishedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFinished", oldToken), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await GiveFireAndForgetContinuationsTimeToRunAsync();

        Assert.Equal(1, Volatile.Read(ref stopReports));
    }

    [Fact]
    public async Task PlaybackStarted_DisplacementFailure_DoesNotTriggerCorrection()
    {
        // JF-425: same displacement classification as PlaybackStoppedEventHandler, applied
        // to PlaybackFailed. A failure for the OLD item while the queue already points at
        // the new one must not be registered as a superseding stop.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopReports = 0;
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Returns(startGate.Task);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Callback(() => Interlocked.Increment(ref stopReports))
            .Returns(Task.CompletedTask);

        string oldToken = Guid.NewGuid().ToString();
        var context = CreateContextForFreshDevice();
        var queue = _queueManager.GetOrCreateQueue(context.System!.Device!.DeviceID);
        queue.ItemIds = new List<string> { Guid.NewGuid().ToString() };
        queue.CurrentIndex = 0;
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", oldToken), context, user, CreateSession(), CancellationToken.None);
        await CreateFailedHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackFailed", oldToken), context, user, CreateSession(), CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref stopReports));

        startGate.TrySetResult();
        await GiveFireAndForgetContinuationsTimeToRunAsync();

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
        var queue = _queueManager.GetOrCreateQueue(context.System!.Device!.DeviceID);
        queue.ItemIds = new List<string> { token };
        queue.CurrentIndex = 0;
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
        string token = $"{itemId}|sleep:12345";
        var context = CreateContextForFreshDevice();
        var user = TestHelpers.CreateTestUser();

        await CreateStartHandler().HandleAsync(
            CreateAudioPlayerRequest("AudioPlayer.PlaybackStarted", Guid.NewGuid().ToString()), context, user, CreateSession(), CancellationToken.None);

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

    /// <summary>
    /// Gives fire-and-forget continuations (released gates) a grace period to run before
    /// asserting that they did NOT issue a corrective call. Only used for absence checks.
    /// </summary>
    private static async Task GiveFireAndForgetContinuationsTimeToRunAsync()
        => await Task.Delay(300);

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

    [Fact]
    public void PlaybackStopped_CanHandle_ReturnsTrueForPlaybackStopped()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);
        var request = CreateAudioPlayerRequest("AudioPlayer.PlaybackStopped");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlaybackStopped_Handle_ReturnsEmptyResponse()
    {
        var handler = new PlaybackStoppedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object);
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

    [Fact]
    public async Task ExceptionHandler_Handle_ReturnsErrorMessage()
    {
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var response = await handler.HandleAsync(
            new SystemExceptionRequest { Error = new Error { Message = "test error" } },
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("wrong", speech.Text, StringComparison.OrdinalIgnoreCase);
    }
}
