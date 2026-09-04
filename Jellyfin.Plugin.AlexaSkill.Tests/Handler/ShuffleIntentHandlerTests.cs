using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for ShuffleOnIntentHandler and ShuffleOffIntentHandler: authoritative
/// per-device shuffle state + queue reshuffle/restore (issue #10 follow-up).
/// </summary>
public class ShuffleIntentHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ShuffleIntentHandlerTests()
    {
        // Registered mint (JF-486 belt): swept at process exit even if a queue
        // flush slips past the per-test dispose below.
        _tempDir = TestHelpers.CreateRegisteredTempDir("shuffle-test");
        _sessionManagerMock = new Mock<ISessionManager>();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    public void Dispose()
    {
        // Each test disposes its own DeviceQueueManager (using declaration) BEFORE
        // this runs: a live manager's 2s debounce flush would recreate the dir (JF-486).
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IntentRequest ShuffleRequest(string intentName) =>
        new() { Intent = new Intent { Name = intentName }, Locale = "en-US", RequestId = "test" };

    private static Context ContextWithToken(string token, string deviceId = "test-device")
    {
        Context c = TestHelpers.CreateTestContext(deviceId);
        c.AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 0 };
        return c;
    }

    private SessionInfo NewSession(IEnumerable<Guid> ids)
    {
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.NowPlayingQueue = ids.Select(g => new QueueItem { Id = g }).ToList();
        return session;
    }

    // =====================================================================
    // ShuffleOn
    // =====================================================================

    [Fact]
    public async Task ShuffleOn_SetsAuthoritativeState_AndReshufflesQueueTail()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        List<Guid> guids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        List<string> ids = guids.Select(g => g.ToString()).ToList();
        mgr.SetQueue("test-device", ids, currentIndex: 0);
        SessionInfo session = NewSession(guids);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        Context context = ContextWithToken(guids[0].ToString());

        await handler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOn), context, TestHelpers.CreateTestUser(), session, default);

        DeviceQueue q = mgr.GetOrCreateQueue("test-device");
        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(guids[0].ToString(), q.ItemIds[0]);       // current stays first
        Assert.Equal(10, q.ItemIds.Count);

        // session.NowPlayingQueue mirrored into the new (shuffled) order
        Assert.Equal(guids[0], session.NowPlayingQueue[0].Id);
        Assert.Equal(10, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task ShuffleOn_ShortQueue_StillSetsFlag_NoReshuffle()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        List<Guid> guids = new() { Guid.NewGuid(), Guid.NewGuid() };
        mgr.SetQueue("test-device", guids.Select(g => g.ToString()).ToList(), 0);
        SessionInfo session = NewSession(guids);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await handler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOn), ContextWithToken(guids[0].ToString()), TestHelpers.CreateTestUser(), session, default);

        DeviceQueue q = mgr.GetOrCreateQueue("test-device");
        Assert.Equal("Shuffle", q.PlaybackOrder);              // flag set even when no-op reshuffle
        Assert.Null(q.OriginalItemIds);                        // too short to reshuffle
    }

    [Fact]
    public async Task ShuffleOn_WithoutQueueManager_DoesNotThrow()
    {
        SessionInfo session = NewSession(new[] { Guid.NewGuid() });
        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            ContextWithToken(session.NowPlayingQueue[0].Id.ToString()),
            TestHelpers.CreateTestUser(), session, default);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task ShuffleOn_QueueWithDuplicateTrackIds_DoesNotThrow()
    {
        // Playlists may contain the same song more than once → NowPlayingQueue holds
        // duplicate Guid IDs. MirrorQueueToSession must not throw on duplicate keys.
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        Guid dup = Guid.NewGuid();
        List<Guid> guids = new() { dup, Guid.NewGuid(), dup, Guid.NewGuid(), Guid.NewGuid(), dup };
        mgr.SetQueue("test-device", guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = NewSession(guids);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            ContextWithToken(dup.ToString()),
            TestHelpers.CreateTestUser(), session, default);

        Assert.NotNull(response);
        Assert.True(session.NowPlayingQueue.Count >= guids.Count); // nothing dropped by the mirror
    }

    // =====================================================================
    // ShuffleOff
    // =====================================================================

    [Fact]
    public async Task ShuffleOff_RestoresOriginalOrder_AfterShuffleOn()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        List<Guid> guids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        List<string> ids = guids.Select(g => g.ToString()).ToList();
        mgr.SetQueue("test-device", ids, currentIndex: 0);
        SessionInfo session = NewSession(guids);

        var onHandler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await onHandler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOn), ContextWithToken(guids[0].ToString()), TestHelpers.CreateTestUser(), session, default);

        var offHandler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await offHandler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOff), ContextWithToken(guids[0].ToString()), TestHelpers.CreateTestUser(), session, default);

        DeviceQueue q = mgr.GetOrCreateQueue("test-device");
        Assert.Equal("Default", q.PlaybackOrder);
        Assert.Null(q.OriginalItemIds);
        Assert.Equal(ids, q.ItemIds);                          // back to original sequence
    }

    [Fact]
    public async Task ShuffleOff_WithoutQueueManager_DoesNotThrow()
    {
        SessionInfo session = NewSession(new[] { Guid.NewGuid() });
        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            ContextWithToken(session.NowPlayingQueue[0].Id.ToString()),
            TestHelpers.CreateTestUser(), session, default);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task ShuffleOff_ResyncsCurrentIndex_ToCurrentlyPlayingItem()
    {
        // Regression: RestoreOrder reverts ItemIds to original order, which moves the
        // playing item to a different index. ShuffleOff must MoveTo() the playing item
        // so persisted CurrentIndex (read by PlaybackStoppedEventHandler) stays correct.
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        List<Guid> guids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        List<string> ids = guids.Select(g => g.ToString()).ToList();
        mgr.SetQueue("test-device", ids, currentIndex: 0);

        // Known shuffled state; playback has advanced so guids[1] is playing at index 2.
        DeviceQueue dq = mgr.GetQueue("test-device")!;
        Guid[] reshuffled = { guids[0], guids[3], guids[1], guids[4], guids[2] };
        dq.ItemIds = reshuffled.Select(g => g.ToString()).ToList();
        dq.OriginalItemIds = new List<string>(ids);
        dq.PlaybackOrder = "Shuffle";
        dq.CurrentIndex = 2;
        Guid playingNow = guids[1];

        SessionInfo session = NewSession(reshuffled);

        var offHandler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await offHandler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            ContextWithToken(playingNow.ToString()),
            TestHelpers.CreateTestUser(), session, default);

        DeviceQueue q = mgr.GetOrCreateQueue("test-device");
        Assert.Equal(ids, q.ItemIds);                  // original order restored
        Assert.Equal(1, q.CurrentIndex);               // guids[1] is at index 1 in the original order
        Assert.Equal(ids.IndexOf(playingNow.ToString()), q.CurrentIndex);
    }

    // JF-424.1: enabling shuffle changes which item follows the current one, so the
    // device's pre-computed sequential next-track entry must be dropped.
    [Fact]
    public async Task ShuffleOn_InvalidatesPrecomputeCache()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        Guid current = Guid.NewGuid();
        Guid cachedNext = Guid.NewGuid();
        string deviceId = "shuffle-jf4241-" + Guid.NewGuid().ToString("N");
        mgr.SetQueue(deviceId, new List<string> { current.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() }, 0);
        NextTrackPrecomputeCache.Store(
            deviceId, current.ToString(), cachedNext,
            new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Cached Next", Id = cachedNext }, "https://stream/next");

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            ContextWithToken(current.ToString(), deviceId),
            TestHelpers.CreateTestUser(), NewSession(new[] { current }), default);

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, current.ToString(), out _, out _, out _));
    }

    // JF-424.1: restoring the original order on shuffle-off equally changes which item
    // follows the current one (an entry pre-computed under shuffle order is stale).
    [Fact]
    public async Task ShuffleOff_InvalidatesPrecomputeCache()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        Guid current = Guid.NewGuid();
        Guid cachedNext = Guid.NewGuid();
        string deviceId = "shuffle-jf4241-" + Guid.NewGuid().ToString("N");
        List<string> original = new() { current.ToString(), cachedNext.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };

        // Known shuffled state: tail reordered, original order snapshotted.
        DeviceQueue dq = mgr.GetOrCreateQueue(deviceId);
        dq.ItemIds = new List<string> { current.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        dq.OriginalItemIds = new List<string>(original);
        dq.PlaybackOrder = "Shuffle";
        dq.CurrentIndex = 0;
        NextTrackPrecomputeCache.Store(
            deviceId, current.ToString(), cachedNext,
            new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Cached Next", Id = cachedNext }, "https://stream/next");

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, mgr);
        await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            ContextWithToken(current.ToString(), deviceId),
            TestHelpers.CreateTestUser(), NewSession(new[] { current }), default);

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, current.ToString(), out _, out _, out _));
    }
}
