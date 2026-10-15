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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
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
    private readonly Mock<ILibraryManager> _libraryManager;
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
        _libraryManager = new Mock<ILibraryManager>();
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


    // Live battery 2026-09-23: «mescola la coda» needs the custom it-IT twin name;
    // both names must route to the same shuffle handlers (the LoopAllOn precedent).
    [Theory]
    [InlineData("ShuffleAllOnIntent")]
    [InlineData("AMAZON.ShuffleOnIntent")]
    public void CanHandle_AcceptsBothShuffleOnNames(string intentName)
    {
        var onHandler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory);
        Assert.True(onHandler.CanHandle(ShuffleRequest(intentName)));
    }

    [Theory]
    [InlineData("ShuffleAllOffIntent")]
    [InlineData("AMAZON.ShuffleOffIntent")]
    public void CanHandle_AcceptsBothShuffleOffNames(string intentName)
    {
        var offHandler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory);
        Assert.True(offHandler.CanHandle(ShuffleRequest(intentName)));
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

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
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

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        await handler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOn), ContextWithToken(guids[0].ToString()), TestHelpers.CreateTestUser(), session, default);

        DeviceQueue q = mgr.GetOrCreateQueue("test-device");
        Assert.Equal("Shuffle", q.PlaybackOrder);              // flag set even when no-op reshuffle
        Assert.Null(q.OriginalItemIds);                        // too short to reshuffle
    }

    [Fact]
    public async Task ShuffleOn_WithoutQueueManager_DoesNotThrow()
    {
        SessionInfo session = NewSession(new[] { Guid.NewGuid() });
        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory);
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

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
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

        var onHandler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        await onHandler.HandleAsync(ShuffleRequest(IntentNames.AmazonShuffleOn), ContextWithToken(guids[0].ToString()), TestHelpers.CreateTestUser(), session, default);

        var offHandler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
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
        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory);
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

        var offHandler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
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

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            ContextWithToken(current.ToString(), deviceId),
            TestHelpers.CreateTestUser(), NewSession(new[] { current }), default);

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, current.ToString(), out _, out _, out _, out _));
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

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            ContextWithToken(current.ToString(), deviceId),
            TestHelpers.CreateTestUser(), NewSession(new[] { current }), default);

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, current.ToString(), out _, out _, out _, out _));
    }

    // =====================================================================
    // JF-890: the loop/shuffle write-family medium gate (stale VideoApp token)
    // =====================================================================

    /// <summary>
    /// THE JF-890 regression pin, shuffle edition (live corr 847bcda6 was the
    /// loop ask; the shuffle twins share the ONE gate): playerActivity=STOPPED
    /// over the video token frozen at the last stop, an empty session, and a
    /// fresh VideoApp-routed movie ledger. Nothing is playing, so the honest
    /// answer is the no-media tell and NO write happens: the queue keeps its
    /// order (no SetPlaybackOrder, no progress report, no reshuffle).
    /// </summary>
    [Fact]
    public async Task ShuffleOn_StaleStoppedVideoToken_EmptySession_NoMediaNoWrite_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var movie = TestHelpers.CreateMovie("Sunny Dancer");
        TestHelpers.SetupLibraryResolves(_libraryManager, movie);
        string deviceId = "shuffle-stale-jf890-device";
        mgr.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(movie.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.DoesNotContain(
            ResponseStrings.Get("CannotShuffleContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Default", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Guard c, shuffle edition: the same video shape with the device reporting
    /// PLAYING keeps the honest video-family refusal (the shuffle twin of the
    /// loop strings).
    /// </summary>
    [Fact]
    public async Task ShuffleOn_ActiveVideoPlaying_Refuses_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var movie = TestHelpers.CreateMovie("Active Movie");
        TestHelpers.SetupLibraryResolves(_libraryManager, movie);
        string deviceId = "shuffle-active-jf890-device";
        mgr.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(movie.Id.ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("CannotShuffleContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The string-decision pin (the loop family's seek-mode split, mirrored): a
    /// VideoAppAudio medium (the same-item seek shape: the token names the SONG
    /// the VideoApp route re-recorded) refuses with the SEEK-MODE line, not the
    /// video-family line, because the content IS music.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_SeekModeVideoAppAudioPlaying_RefusesWithSeekLine_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var seekSong = TestHelpers.CreateSong("Seek Mode Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, seekSong);
        string deviceId = "shuffle-seek-jf890-device";
        mgr.RecordLastPlayed(deviceId, seekSong.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(seekSong.Id.ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("CannotShuffleInSeekMode", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.DoesNotContain(
            ResponseStrings.Get("CannotShuffleContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Guard b, shuffle edition: active music (PLAYING + audio token over a real
    /// queue) still shuffles: the gate passes Audio/Unknown through untouched.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_ActiveMusicPlaying_StillShuffles_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        List<Guid> guids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        string deviceId = "shuffle-music-jf890-device";
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = NewSession(guids);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(guids[0].ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("ShuffleEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Shuffle", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
    }

    /// <summary>
    /// The seek-mode arm of the JF-890 stale shape (code-review finding): a
    /// SONG recorded on the VideoApp route (the belt re-classifies the
    /// classifier's Audio answer to VideoAppAudio), device reports STOPPED,
    /// empty session. Nothing is playing: the no-media tell, never the
    /// seek-mode refusal, and no write.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_StaleStoppedSeekModeToken_EmptySession_NoMediaNoWrite_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var seekSong = TestHelpers.CreateSong("Stale Seek Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, seekSong);
        string deviceId = "shuffle-stale-seek-jf890-device";
        mgr.RecordLastPlayed(deviceId, seekSong.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(seekSong.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.DoesNotContain(
            ResponseStrings.Get("CannotShuffleInSeekMode", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Default", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The JF-890 pin for the OFF twin: on the stale STOPPED video residue the
    /// shuffle-off answers the no-media tell and does NOT touch the queue state
    /// (an already-shuffled queue keeps its shuffle: no SetPlaybackOrder, no
    /// RestoreOrder, no progress report).
    /// </summary>
    [Fact]
    public async Task ShuffleOff_StaleStoppedVideoToken_EmptySession_NoMediaNoRestore_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var movie = TestHelpers.CreateMovie("Sunny Dancer");
        TestHelpers.SetupLibraryResolves(_libraryManager, movie);
        string deviceId = "shuffle-off-stale-jf890-device";
        mgr.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        List<Guid> guids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        mgr.SetPlaybackOrder(deviceId, "Shuffle");
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            TestHelpers.CreateContextWithToken(movie.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        DeviceQueue q = mgr.GetOrCreateQueue(deviceId);
        Assert.Equal("Shuffle", q.PlaybackOrder);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The OFF twin's active refusal: PLAYING over the video medium keeps the
    /// honest shuffle refusal line.
    /// </summary>
    [Fact]
    public async Task ShuffleOff_ActiveVideoPlaying_Refuses_JF890()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var movie = TestHelpers.CreateMovie("Active Movie");
        TestHelpers.SetupLibraryResolves(_libraryManager, movie);
        string deviceId = "shuffle-off-active-jf890-device";
        mgr.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            TestHelpers.CreateContextWithToken(movie.Id.ToString(), deviceId, "PLAYING"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("CannotShuffleContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    // =====================================================================
    // JF-892: null-context NRE, stale AUDIO token, cold device
    // =====================================================================

    /// <summary>
    /// The JF-450 mirror for the twins: a request whose context carries NO
    /// AudioPlayer object (Alexa.NET leaves it null; the open-session
    /// nothing-playing shape) must answer the no-media tell, never the NRE the
    /// old non-nullable token read threw. No write: no queue is created and no
    /// progress is reported.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_NoAudioPlayerContext_NoMediaNoWrite_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        string deviceId = "shuffle-nullctx-jf892-device";
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateTestContext(deviceId), // no AudioPlayer object at all
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Null(mgr.GetQueue(deviceId)); // no empty queue created or persisted
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The OFF twin of the JF-450 mirror: same null-AudioPlayer shape, same
    /// no-media tell, and additionally no RestoreOrder side effect.
    /// </summary>
    [Fact]
    public async Task ShuffleOff_NoAudioPlayerContext_NoMediaNoWrite_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        string deviceId = "shuffle-off-nullctx-jf892-device";
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            TestHelpers.CreateTestContext(deviceId), // no AudioPlayer object at all
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Null(mgr.GetQueue(deviceId));
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// THE JF-892 defect-2 pin, ON twin: an idle device after music keeps the
    /// song token frozen with an explicit halt report (the audio twin of the
    /// JF-890 video residue; every ReportsPlaybackHalted activity, so a future
    /// edit that drops an arm goes red here). The gate's Audio staleness bound
    /// must answer the no-media tell and skip the whole write: no progress
    /// report on the dead item, no SetPlaybackOrder, no physical reshuffle
    /// (the queue keeps its original order).
    /// </summary>
    [Theory]
    [InlineData("STOPPED")]
    [InlineData("FINISHED")]
    [InlineData("IDLE")]
    public async Task ShuffleOn_HaltReportedAudioToken_EmptySession_NoMediaNoWrite_JF892(string playerActivity)
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Frozen Token Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-stale-audio-jf892-device";
        mgr.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        List<Guid> guids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        List<string> ids = guids.Select(g => g.ToString()).ToList();
        mgr.SetQueue(deviceId, ids, currentIndex: 0);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, playerActivity),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        DeviceQueue q = mgr.GetOrCreateQueue(deviceId);
        Assert.Equal("Default", q.PlaybackOrder);   // flag never written
        Assert.Equal(ids, q.ItemIds);               // tail never reshuffled
        Assert.Null(q.OriginalItemIds);             // no shuffle snapshot taken
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The code-review arm of the JF-892 bound: the same halt-reported frozen
    /// token with NO ledger entry (the wiped-ledger idle shape) classifies
    /// Unknown, and the residue bound must cover it too; without the non-VideoApp
    /// scope the write would slip past the gate and the idle guard's token leg.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_HaltReportedTokenNoLedger_NoMediaNoWrite_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Ledgerless Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-noledger-jf892-device";
        List<Guid> guids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        DeviceQueue q = mgr.GetOrCreateQueue(deviceId);
        Assert.Equal("Default", q.PlaybackOrder);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// THE JF-892 defect-2 pin, OFF twin: the same stale audio residue must not
    /// restore anything; an already-shuffled queue keeps its shuffle (no
    /// SetPlaybackOrder to Default, no RestoreOrder, no index re-sync, no
    /// progress report).
    /// </summary>
    [Fact]
    public async Task ShuffleOff_StaleStoppedAudioToken_EmptySession_NoMediaNoRestore_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Frozen Token Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-off-stale-audio-jf892-device";
        mgr.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        List<Guid> guids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        List<string> ids = guids.Select(g => g.ToString()).ToList();
        mgr.SetQueue(deviceId, ids, currentIndex: 0);

        // Known shuffled state over the snapshot (the RestoreOrder precondition):
        // if the handler wrongly ran, ItemIds reverts to ids and the snapshot
        // is consumed to null.
        DeviceQueue dq = mgr.GetOrCreateQueue(deviceId);
        List<string> shuffledOrder = new() { ids[0], ids[3], ids[1], ids[2] };
        dq.ItemIds = shuffledOrder;
        dq.OriginalItemIds = new List<string>(ids);
        dq.PlaybackOrder = "Shuffle";
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        DeviceQueue q = mgr.GetOrCreateQueue(deviceId);
        Assert.Equal("Shuffle", q.PlaybackOrder);   // flag never cleared
        Assert.Equal(shuffledOrder, q.ItemIds);     // order never restored
        Assert.Equal(ids, q.OriginalItemIds);       // snapshot never consumed
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// THE JF-892 defect-3 pin: a COLD device (an AudioPlayer object present
    /// but carrying no token at all, the fresh-context shape) answers the
    /// no-media tell and the queue manager is never touched: no empty queue is
    /// created or persisted (GetOrCreateQueue inside SetPlaybackOrder is the
    /// old write path).
    /// </summary>
    [Fact]
    public async Task ShuffleOn_ColdDeviceNoToken_NoMediaNoEmptyQueuePersisted_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        string deviceId = "shuffle-cold-jf892-device";
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(null, deviceId, "IDLE"), // AudioPlayer present, token absent
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Null(mgr.GetQueue(deviceId)); // the manager was never touched
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The defect-3 pin, OFF twin: same cold-device shape, no empty queue, no
    /// RestoreOrder.
    /// </summary>
    [Fact]
    public async Task ShuffleOff_ColdDeviceNoToken_NoMediaNoEmptyQueuePersisted_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        string deviceId = "shuffle-off-cold-jf892-device";
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOff),
            TestHelpers.CreateContextWithToken(null, deviceId, "IDLE"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Null(mgr.GetQueue(deviceId));
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The JF-892 boundary pin, pause arm: PAUSED music is a loaded, resumable
    /// stream, not a halt, so shuffle still works while paused (the Audio
    /// staleness bound must not over-refuse).
    /// </summary>
    [Fact]
    public async Task ShuffleOn_PausedMusic_StillShuffles_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Paused Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-paused-jf892-device";
        mgr.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        List<Guid> guids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, "PAUSED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("ShuffleEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Shuffle", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
    }

    /// <summary>
    /// The JF-892 boundary pin, flag arm (the JF-655 signal): an explicit halt
    /// report does NOT make the verdict stale while the event-owned flag says a
    /// stream is live (the queue-advance race: Finished(old) reported while
    /// Started(new) already fired), so the write still runs.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_HaltedReportButActiveFlag_StillShuffles_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Race Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-flag-jf892-device";
        mgr.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        mgr.MarkAudioPlaybackStarted(deviceId);
        List<Guid> guids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("ShuffleEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Shuffle", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
    }

    /// <summary>
    /// The JF-892 boundary pin, held-item arm (the JF-868 mirror): a bare
    /// STOPPED never proves idleness while the session still holds the item, so
    /// the write still runs.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_HaltedReportSessionHoldsSong_StillShuffles_JF892()
    {
        using DeviceQueueManager mgr = new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        var song = TestHelpers.CreateSong("Held Song");
        TestHelpers.SetupLibraryResolves(_libraryManager, song);
        string deviceId = "shuffle-held-jf892-device";
        mgr.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        List<Guid> guids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        guids[0] = song.Id;
        mgr.SetQueue(deviceId, guids.Select(g => g.ToString()).ToList(), currentIndex: 0);
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.FullNowPlayingItem = song;

        var handler = new ShuffleOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, mgr);
        SkillResponse response = await handler.HandleAsync(
            ShuffleRequest(IntentNames.AmazonShuffleOn),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(), session, default);

        Assert.Contains(
            ResponseStrings.Get("ShuffleEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Equal("Shuffle", mgr.GetOrCreateQueue(deviceId).PlaybackOrder);
    }
}
