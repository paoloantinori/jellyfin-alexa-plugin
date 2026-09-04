#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-390: PreEnqueueOnStart eliminates the timing-dependent PlaybackNearlyFinished
/// round-trip by pre-enqueueing the next track when the current one STARTS playing.
/// When on, PlaybackStarted returns an AudioPlayer.Play (Enqueue) directive for the
/// next queue item instead of just a keep-alive ack.
/// </summary>
[Collection("Plugin")]
public class PreEnqueueOnStartTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _tempDir;

    public PreEnqueueOnStartTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        _tempDir = Path.Combine(Path.GetTempPath(), $"precompute-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup only
        }

        GC.SuppressFinalize(this);
    }

    private DeviceQueueManager CreateQueueManager()
        => new(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());

    private PlaybackStartedEventHandler CreateHandler()
    {
        return new PlaybackStartedEventHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            _libraryManagerMock.Object);
    }

    private static AudioPlayerRequest CreateStartedRequest(string token, long offsetMs = 0)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = token,
            OffsetInMilliseconds = offsetMs,
            RequestId = "test-req"
        };
    }

    private static Context CreateContext(string? token = null)
    {
        var context = TestHelpers.CreateTestContext();
        if (token != null)
        {
            context.AudioPlayer = new PlaybackState
            {
                Token = token,
                OffsetInMilliseconds = 0
            };
        }

        return context;
    }

    private static Context CreateDeviceContext(string token, string deviceId)
    {
        var context = CreateContext(token);
        context.System.Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId };
        return context;
    }

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string token)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token,
            OffsetInMilliseconds = 0,
            RequestId = "test-req"
        };
    }

    private SessionInfo CreateSession(List<QueueItem>? queue = null, Guid? currentItem = null)
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        if (queue != null)
        {
            session.NowPlayingQueue = queue;
        }

        if (currentItem.HasValue)
        {
            session.FullNowPlayingItem = new Audio { Name = "Current", Id = currentItem.Value };
        }

        return session;
    }

    private void SetupLibraryItem(Guid id, string name)
    {
        _libraryManagerMock.Setup(l => l.GetItemById(id))
            .Returns(new Audio { Name = name, Id = id });
    }

    // When the knob is OFF (default), PlaybackStarted returns a keep-alive ack
    // (existing behavior, unchanged).
    [Fact]
    public async Task PlaybackStarted_KnobOff_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = false;
        var handler = CreateHandler();
        var request = CreateStartedRequest(Guid.NewGuid().ToString());
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }

    // When the knob is ON and there is a next item in the queue, PlaybackStarted
    // pre-computes it into NextTrackPrecomputeCache (NOT an AudioPlayer.Play response,
    // which Amazon rejects for this event type). The response is a keep-alive; the
    // cache entry is consumed by PlaybackNearlyFinished for an instant response.
    [Fact]
    public async Task PlaybackStarted_KnobOn_NextInQueue_PopulatesPrecomputeCache()
    {
        _config.PreEnqueueOnStart = true;
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        SetupLibraryItem(nextId, "Next Track");
        var deviceId = "test-device-precompute";

        var handler = CreateHandler();
        var request = CreateStartedRequest(currentId.ToString());
        var session = CreateSession(
            new List<QueueItem>
            {
                new() { Id = currentId },
                new() { Id = nextId }
            },
            currentId);

        var context = CreateContext(currentId.ToString());
        context.System.Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId };

        SkillResponse response = await handler.HandleAsync(
            request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Response is keep-alive (NOT AudioPlayer.Play; Amazon rejects it for PlaybackStarted)
        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");

        // But the cache has the pre-computed next track
        Assert.True(Jellyfin.Plugin.AlexaSkill.Alexa.Playback.NextTrackPrecomputeCache.TryGet(
            deviceId, currentId.ToString(), out Guid cachedId, out _, out _));
        Assert.Equal(nextId, cachedId);

        // Cleanup
        Jellyfin.Plugin.AlexaSkill.Alexa.Playback.NextTrackPrecomputeCache.Invalidate(deviceId);
    }

    // When the knob is ON but the current track is the LAST in the queue,
    // PlaybackStarted returns a keep-alive (nothing to pre-enqueue).
    [Fact]
    public async Task PlaybackStarted_KnobOn_LastInQueue_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = true;
        var currentId = Guid.NewGuid();
        var handler = CreateHandler();
        var request = CreateStartedRequest(currentId.ToString());
        var session = CreateSession(
            new List<QueueItem> { new() { Id = currentId } },
            currentId);

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(currentId.ToString()), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }

    // When the knob is ON but the queue is empty, keep-alive.
    [Fact]
    public async Task PlaybackStarted_KnobOn_EmptyQueue_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = true;
        var handler = CreateHandler();
        var request = CreateStartedRequest(Guid.NewGuid().ToString());
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }

    // JF-409: the cache is documented as keyed by (deviceId, currentTrackToken) but the
    // implementation keyed by deviceId alone, so an entry stored for a PREVIOUS track
    // could be served for the current one and re-enqueue it on itself.
    [Fact]
    public void Cache_TryGet_WithDifferentCurrentToken_MissesEvenWithinTtl()
    {
        var deviceId = "device-jf409-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var tokenB = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        NextTrackPrecomputeCache.Store(deviceId, tokenA, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenB, out _, out _, out _));

    }

    // JF-424: Amazon multi-fires NearlyFinished. A late duplicate NearlyFinished(trackA)
    // arriving AFTER PlaybackStarted(trackB) stored entry(trackB -> trackC) must not
    // consume that fresh entry via its token mismatch: the real NearlyFinished(trackB)
    // still needs it, otherwise it does the full library+stream-URL resolution (the
    // 11-20s stall JF-390 exists to avoid).
    [Fact]
    public void Cache_TryGet_TokenMismatch_LeavesStoredEntryForMatchingConsumer()
    {
        var deviceId = "device-jf424-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var tokenB = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        NextTrackPrecomputeCache.Store(deviceId, tokenB, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        // Late/duplicate NearlyFinished for the PREVIOUS track: miss, and must NOT consume.
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));

        // The fresh entry survives for the CURRENT track's real NearlyFinished.
        Assert.True(NextTrackPrecomputeCache.TryGet(deviceId, tokenB, out Guid cachedId, out _, out string? streamUrl));
        Assert.Equal(nextId, cachedId);
        Assert.Equal("https://stream/next", streamUrl);

    }

    // JF-424.2 (AC#2): an entry past its 15-minute TTL is dead on ANY read. With a
    // MATCHING token the read misses AND reclaims the entry. Reclamation is proven
    // through the public surface: the clock is then wound back inside the entry's
    // validity window, where a matching-token read would hit if the entry were still
    // stored; it misses, so the expired read removed it.
    [Fact]
    public void Cache_TryGet_ExpiredEntry_MatchingToken_MissesAndReclaimsEntry()
    {
        var deviceId = "device-jf4242-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var fake = new FakeTimeProvider(t0);
        NextTrackPrecomputeCache.Store(deviceId, tokenA, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        // One minute past the 15-minute TTL: dead entry, matching token.
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(16));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));

        // Back inside the window: still a miss, so the entry was reclaimed above.
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(1));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));
    }

    // JF-424.2 (AC#3): the TTL check runs BEFORE the token check, so an expired entry
    // is reclaimed even by a MISMATCHED-token read. A refactor that swaps the two
    // checks, or drops the remove-on-expired, would return the mismatch miss while
    // leaving the dead entry resident; the second read below catches both shapes.
    [Fact]
    public void Cache_TryGet_ExpiredEntry_MismatchedToken_MissesAndReclaimsEntry()
    {
        var deviceId = "device-jf4242-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var tokenB = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var fake = new FakeTimeProvider(t0);
        NextTrackPrecomputeCache.Store(deviceId, tokenB, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        // One minute past the 15-minute TTL: dead entry, mismatched token.
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(16));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));

        // Back inside the window, the entry's OWN token must miss too: the expired
        // mismatched read reclaimed the dead entry instead of leaving it for a
        // later (wrongly fresh-looking) serve.
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(1));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenB, out _, out _, out _));
    }

    // JF-424.2 (AC#4): the JF-424 retention invariant pinned at an EXPLICIT clock
    // position inside the TTL (the TokenMismatch test above is fresh only by
    // test-execution speed). At 14 of the 15 minutes the entry is still fresh: a
    // mismatched read misses but must NOT reclaim it, and the matching read that
    // follows still hits (which also pins fresh + matching token = hit through the seam).
    [Fact]
    public void Cache_TryGet_WithinTtl_MismatchedToken_MissesButRetainsEntry()
    {
        var deviceId = "device-jf4242-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var tokenB = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var fake = new FakeTimeProvider(t0);
        NextTrackPrecomputeCache.Store(deviceId, tokenB, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        // Inside the window, mismatched token: miss, entry retained (JF-424).
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(14));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));

        // Still inside the window: the matching consumer hits the retained entry.
        fake.SetUtcNow(t0 + TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(30));
        Assert.True(NextTrackPrecomputeCache.TryGet(deviceId, tokenB, out Guid cachedId, out _, out string? streamUrl));
        Assert.Equal(nextId, cachedId);
        Assert.Equal("https://stream/next", streamUrl);
    }

    // JF-424 handler-level replay of the live interleaving: NearlyFinished(A) consumed
    // entry(A->B), Started(B) stored entry(B->C), then Amazon re-sent a late duplicate
    // NearlyFinished(A). The duplicate's mismatched probe must leave entry(B->C) in
    // place so the REAL NearlyFinished(B) still answers from the cache. The library
    // stops resolving C before the duplicate arrives, so trackC can only come from the
    // precomputed entry: the full-resolution fallback cannot produce it.
    [Fact]
    public async Task PlaybackNearlyFinished_DuplicateForPreviousTrack_DoesNotDestroyFreshEntry()
    {
        _config.PreEnqueueOnStart = true;
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        var trackC = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        SetupLibraryItem(trackC, "Track C");
        var deviceId = "device-jf424-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var queue = new List<QueueItem> { new() { Id = trackA }, new() { Id = trackB }, new() { Id = trackC } };
        var startedHandler = CreateHandler();
        var nearlyFinishedHandler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, new Mock<IUserManager>().Object, _loggerFactory);

        // 1) Started(A) stores entry(A->B); NearlyFinished(A) consumes it (normal flow).
        await startedHandler.HandleAsync(
            CreateStartedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackA), CancellationToken.None);
        await nearlyFinishedHandler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackA), CancellationToken.None);

        // 2) Started(B) stores entry(B->C).
        await startedHandler.HandleAsync(
            CreateStartedRequest(trackB.ToString()), CreateDeviceContext(trackB.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackB), CancellationToken.None);

        // 3) From here on only the precomputed entry can answer with trackC.
        _libraryManagerMock.Setup(l => l.GetItemById(trackC)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        // 4) Late duplicate NearlyFinished(A): token mismatch, must NOT consume entry(B->C).
        await nearlyFinishedHandler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackB), CancellationToken.None);

        // 5) The REAL NearlyFinished(B) still serves the precomputed trackC from the cache.
        SkillResponse response = await nearlyFinishedHandler.HandleAsync(
            CreateNearlyFinishedRequest(trackB.ToString()), CreateDeviceContext(trackB.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackB), CancellationToken.None);

        var play = (response.Response?.Directives ?? new List<IDirective>())
            .OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(play);
        Assert.Equal(trackC.ToString(), play.AudioItem?.Stream?.Token);

    }

    // JF-409 regression guard: the matching-token lookup must keep hitting.
    [Fact]
    public void Cache_TryGet_WithSameCurrentToken_Hits()
    {
        var deviceId = "device-jf409-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        NextTrackPrecomputeCache.Store(deviceId, tokenA, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        Assert.True(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out Guid cachedId, out _, out string? streamUrl));
        Assert.Equal(nextId, cachedId);
        Assert.Equal("https://stream/next", streamUrl);

    }

    // JF-409: a stored entry is single-shot. On-device, PlaybackNearlyFinished consumed
    // the entry for track N-1 but nothing removed it, so a later NearlyFinished for the
    // SAME context could serve it again (and re-enqueue a track on itself).
    [Fact]
    public void Cache_TryGet_ConsumesEntry_SingleShot()
    {
        var deviceId = "device-jf409-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var tokenA = Guid.NewGuid().ToString();
        var nextId = Guid.NewGuid();
        NextTrackPrecomputeCache.Store(deviceId, tokenA, nextId, new Audio { Name = "Next", Id = nextId }, "https://stream/next");

        Assert.True(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, tokenA, out _, out _, out _));

    }

    // JF-409 incident replay (live 2026-08-28: "Older Chests" enqueued on itself):
    // Started(trackA) precomputes next=trackB; NearlyFinished(trackA) consumes it;
    // Started(trackB) has nothing to precompute (trackB is last in the not-yet-extended
    // progressive queue). NearlyFinished(trackB) must NOT be served the stale entry.
    [Fact]
    public async Task PlaybackStarted_AfterTransition_DoesNotServeStaleEntryForNewCurrentTrack()
    {
        _config.PreEnqueueOnStart = true;
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        var deviceId = "device-jf409-" + Guid.NewGuid().ToString("N");
        var queue = new List<QueueItem> { new() { Id = trackA }, new() { Id = trackB } };
        var handler = CreateHandler();

        // 1) Started(trackA) -> stores (device, trackA -> trackB)
        var contextA = CreateContext(trackA.ToString());
        contextA.System.Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId };
        await handler.HandleAsync(
            CreateStartedRequest(trackA.ToString()), contextA, TestHelpers.CreateTestUser(),
            CreateSession(queue, trackA), CancellationToken.None);

        // 2) NearlyFinished(trackA) consumes the precomputed entry
        Assert.True(NextTrackPrecomputeCache.TryGet(deviceId, trackA.ToString(), out Guid consumedId, out _, out _));
        Assert.Equal(trackB, consumedId);

        // 3) Started(trackB): last item of the (not yet extended) queue -> nothing stored
        var contextB = CreateContext(trackB.ToString());
        contextB.System.Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId };
        await handler.HandleAsync(
            CreateStartedRequest(trackB.ToString()), contextB, TestHelpers.CreateTestUser(),
            CreateSession(queue, trackB), CancellationToken.None);

        // 4) NearlyFinished(trackB) must NOT get a hit (the live bug re-enqueued trackB here)
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, trackB.ToString(), out _, out _, out _));

    }

    // JF-424.1: the token is the BARE item GUID, so a token match identifies an ITEM,
    // not a playback session. An entry stored against a queue that later changed (here:
    // another item was inserted right after the current one, the PlayNext shape) must
    // NOT be served: the cache-hit path re-checks that the cached item still follows
    // the current item in the live session queue and falls through to full resolution.
    [Fact]
    public async Task PlaybackNearlyFinished_CacheHitButQueueChanged_ServesLiveSuccessorNotCachedNext()
    {
        _config.PreEnqueueOnStart = true;
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        var trackX = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        SetupLibraryItem(trackX, "Track X");
        var deviceId = "device-jf4241-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, new Mock<IUserManager>().Object, _loggerFactory);

        // Stale entry: computed when B followed A. The live queue now has X between them.
        NextTrackPrecomputeCache.Store(
            deviceId, trackA.ToString(), trackB, new Audio { Name = "Track B", Id = trackB }, "https://stream/trackB");
        var session = CreateSession(
            new List<QueueItem> { new() { Id = trackA }, new() { Id = trackX }, new() { Id = trackB } },
            trackA);

        SkillResponse response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Full resolution served A's live successor X; the cached B was not served.
        var play = (response.Response?.Directives ?? new List<IDirective>())
            .OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(play);
        Assert.Equal(trackX.ToString(), play.AudioItem?.Stream?.Token);

    }

    // JF-447 review follow-up on JF-424.1: the cache-hit validation must resolve the
    // current item TOKEN-FIRST, matching the STORE side (TryPrecomputeNext resolves the
    // token alone). When A's start report FAILED and session.FullNowPlayingItem still
    // holds the PREVIOUS queue item Z, the FullNowPlayingItem-first validation computed
    // Z's successor (= A, the playing track itself), rejected the cached entry(B), and
    // the full-resolution fall-through ALSO resolved Z and enqueued A after itself (the
    // JF-409 self-reenqueue class). Pre-JF-424.1 the unconditional cache hit served B
    // correctly in this state; the token-first resolution restores that.
    [Fact]
    public async Task PlaybackNearlyFinished_StaleFullNowPlayingItem_ServesCachedSuccessor()
    {
        _config.PreEnqueueOnStart = true;
        var trackZ = Guid.NewGuid();
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        var deviceId = "device-jf447-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, new Mock<IUserManager>().Object, _loggerFactory);

        // Entry(A->B) was stored by Started(A). The session now holds the STALE
        // now-playing item Z (A's start report failed before the session write landed),
        // while the AudioPlayer token correctly names A.
        NextTrackPrecomputeCache.Store(
            deviceId, trackA.ToString(), trackB, new Audio { Name = "Track B", Id = trackB }, "https://stream/trackB");
        var session = CreateSession(
            new List<QueueItem> { new() { Id = trackZ }, new() { Id = trackA }, new() { Id = trackB } },
            trackZ);

        // Track A is deliberately NOT in the library: if the validation resolved Z
        // (FullNowPlayingItem-first), the fall-through would resolve Z's successor A and
        // fail the item lookup (no play directive); the token-first validation serves
        // the cached B.
        _libraryManagerMock.Setup(l => l.GetItemById(trackA)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        SkillResponse response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var play = (response.Response?.Directives ?? new List<IDirective>())
            .OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(play);
        Assert.Equal(trackB.ToString(), play.AudioItem?.Stream?.Token);

    }

    // JF-424.1 incident replay (the task's failure scenario): queue [A,B];
    // Started(A) stores entry(A->B); the user skips A (no NearlyFinished consumes it),
    // clears the queue, and replays A as a single-item play (Started(A) then stores
    // nothing: A is last in the 1-item queue). Within the TTL, NearlyFinished(A) still
    // token-matches the stale entry; it must NOT enqueue B after a single-item play.
    [Fact]
    public async Task PlaybackNearlyFinished_AfterClearQueueAndSingleItemReplay_DoesNotServeStaleEntry()
    {
        _config.PreEnqueueOnStart = true;
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        var deviceId = "device-jf4241-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        using DeviceQueueManager queueManager = CreateQueueManager();
        queueManager.SetQueue(deviceId, new List<string> { trackA.ToString(), trackB.ToString() }, 0);
        var queue = new List<QueueItem> { new() { Id = trackA }, new() { Id = trackB } };
        var startedHandler = CreateHandler();
        var nearlyFinishedHandler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, new Mock<IUserManager>().Object, _loggerFactory);

        // 1) Started(A) on queue [A,B]: stores entry(A->B).
        await startedHandler.HandleAsync(
            CreateStartedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackA), CancellationToken.None);

        // 2) The user skips A (no NearlyFinished for A) and clears the queue.
        var clearHandler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, queueManager);
        await clearHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" }, Locale = "en-US", RequestId = "clear-req" },
            CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(queue, trackA), CancellationToken.None);

        // 3) Replay A as a single-item play: session queue [A], Started stores nothing.
        var singleItemQueue = new List<QueueItem> { new() { Id = trackA } };
        await startedHandler.HandleAsync(
            CreateStartedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(singleItemQueue, trackA), CancellationToken.None);

        // 4) NearlyFinished(A) token-matches the stale entry but must not serve B:
        // the live queue [A] has no successor, so playback ends (PostPlay Stop default).
        SkillResponse response = await nearlyFinishedHandler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(singleItemQueue, trackA), CancellationToken.None);

        var play = (response.Response?.Directives ?? new List<IDirective>())
            .OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.Null(play);

    }

    // JF-424.1: MoveTo failure (resolved next item absent from the device queue, which
    // happens when the session queue was built by a play path that does not mirror it)
    // must leave the recovery pointer untouched instead of dangling CurrentItemId at an
    // item the queue does not contain. The enqueue itself still happens: the session
    // queue is the authoritative resolution source. Covers both the cache-hit path
    // (AC#5) and the full-resolution sibling branch, which share the defect shape.
    [Theory]
    [InlineData(true)]    // precompute cache entry present: cache-hit path
    [InlineData(false)]   // no cache entry: full-resolution path
    public async Task PlaybackNearlyFinished_MoveToFails_LeavesCurrentItemIdUnchanged(bool usePrecomputeEntry)
    {
        _config.PreEnqueueOnStart = true;
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        var otherX = Guid.NewGuid();
        var otherY = Guid.NewGuid();
        SetupLibraryItem(trackB, "Track B");
        var deviceId = "device-jf4241-" + Guid.NewGuid().ToString("N"); // unique per test: isolation without shared state
        using DeviceQueueManager queueManager = CreateQueueManager();

        // Device queue holds unrelated items: MoveTo(trackB) will fail.
        queueManager.SetQueue(deviceId, new List<string> { otherX.ToString(), otherY.ToString() }, 0);
        string previousPointer = otherX.ToString();
        queueManager.GetOrCreateQueue(deviceId).CurrentItemId = previousPointer;

        if (usePrecomputeEntry)
        {
            NextTrackPrecomputeCache.Store(
                deviceId, trackA.ToString(), trackB, new Audio { Name = "Track B", Id = trackB }, "https://stream/trackB");
        }

        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, new Mock<IUserManager>().Object, _loggerFactory, queueManager);

        SkillResponse response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackA.ToString()), CreateDeviceContext(trackA.ToString(), deviceId),
            TestHelpers.CreateTestUser(), CreateSession(new List<QueueItem> { new() { Id = trackA }, new() { Id = trackB } }, trackA),
            CancellationToken.None);

        // The next track is still enqueued (from the cache on the hit path, from the
        // library on the full-resolution path)...
        var play = (response.Response?.Directives ?? new List<IDirective>())
            .OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(play);
        Assert.Equal(trackB.ToString(), play.AudioItem?.Stream?.Token);

        // ...but the queue pointer was left exactly where it was.
        Assert.Equal(previousPointer, queueManager.GetQueue(deviceId)!.CurrentItemId);

    }

    /// <summary>
    /// Hand-rolled deterministic clock for the JF-424.2 TTL tests:
    /// Microsoft.Extensions.TimeProvider.Testing is not referenced by the test project,
    /// and the needed surface is just a settable UTC now. Parked at the epoch the test
    /// chooses, so Store's ComputedAt stamp and the TTL check read the same clock.
    /// Installing itself in the seam on construction and restoring
    /// <see cref="TimeProvider.System"/> on Dispose keeps the global-state window
    /// scoped to the declaring test.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider, IDisposable
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset start)
        {
            _utcNow = start;
            NextTrackPrecomputeCache.Time = this;
        }

        public void SetUtcNow(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Dispose()
        {
            NextTrackPrecomputeCache.Time = TimeProvider.System;
        }
    }
}
