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
        _tempDir = Path.Combine(Path.GetTempPath(), $"shuffle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sessionManagerMock = new Mock<ISessionManager>();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IntentRequest ShuffleRequest(string intentName) =>
        new() { Intent = new Intent { Name = intentName }, Locale = "en-US", RequestId = "test" };

    private static Context ContextWithToken(string token)
    {
        Context c = TestHelpers.CreateTestContext();
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
        DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
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
        DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
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
        DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
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
        DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
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
        DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
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
}
