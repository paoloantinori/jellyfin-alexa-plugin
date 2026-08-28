#nullable enable
using System;
using System.Collections.Generic;
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
public class PreEnqueueOnStartTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PreEnqueueOnStartTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

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
}
