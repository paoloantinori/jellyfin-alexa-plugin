#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-477: the request-path session lookup (GetSessionByAuthenticationToken) is cached
/// as a live SessionInfo reference keyed by (JellyfinToken, deviceId), with a 2s
/// fast-fail budget on the miss path, PlaybackStarted event warm refresh, and
/// dead-session invalidation on the playback-report failure paths.
/// </summary>
[Collection("Plugin")]
public class SessionReferenceCacheTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });

    public SessionReferenceCacheTests()
    {
        // The cache is process-wide static state; reset so entries from other test
        // classes (or earlier tests here) cannot leak in either direction.
        SessionReferenceCache.Reset();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, _ => { }, "jf477-session-cache");
    }

    /// <summary>
    /// A minimal concrete BaseHandler that records every SessionInfo instance
    /// HandleRequestAsync hands it, so tests can assert lookup behavior at the
    /// HandleRequestAsync seam (cache hit/miss, instance reuse).
    /// </summary>
    private sealed class SessionRecordingHandler : BaseHandler
    {
        public List<SessionInfo> ReceivedSessions { get; } = new();

        public SessionRecordingHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        {
            ReceivedSessions.Add(session);
            return Task.FromResult(ResponseBuilder.Tell("handled"));
        }

        /// <summary>Exposes the protected stop-report path for the invalidation test.</summary>
        public Task CallReportStopOrderedAsync(string deviceId, string? rawToken, PlaybackStopInfo stopInfo)
            => ReportStopOrderedAsync(deviceId, rawToken, stopInfo, "test stop");
    }

    private static IntentRequest CreateIntentRequest()
        => new() { Intent = new Intent { Name = "TestIntent" }, Locale = "en-US", RequestId = "req-jf477" };

    private Context CreateContext(Entities.User user, string deviceId)
        => new()
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
                Device = new Device { DeviceID = deviceId }
            }
        };

    private Entities.User CreateUser(string token)
    {
        var user = TestHelpers.CreateTestUser(jellyfinToken: token);
        _config.Users.Add(user);
        return user;
    }

    private void SetupLookupReturns(params SessionInfo[] sessions)
    {
        int call = 0;
        _sessionManagerMock
            .Setup(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => call < sessions.Length ? sessions[call++] : sessions[^1]);
    }

    // (a) A cache hit issues NO second lookup: the second request reuses the reference.
    [Fact]
    public async Task CacheHit_SecondRequest_SkipsLookupAndServesSameReference()
    {
        var user = CreateUser("jf477-hit-token");
        const string deviceId = "jf477-hit-device";
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.UserId = user.Id; // the real lookup stamps the token owner onto the session
        SetupLookupReturns(session);
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateIntentRequest();

        await handler.HandleRequestAsync(request, CreateContext(user, deviceId), CancellationToken.None);
        await handler.HandleRequestAsync(request, CreateContext(user, deviceId), CancellationToken.None);

        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Once);
        Assert.Equal(2, handler.ReceivedSessions.Count);
        Assert.Same(session, handler.ReceivedSessions[0]);
        Assert.Same(session, handler.ReceivedSessions[1]);
    }

    // (b) An entry past its TTL is a miss: the request refetches.
    [Fact]
    public async Task TtlExpired_Entry_MissesAndRefetches()
    {
        var user = CreateUser("jf477-ttl-token");
        const string deviceId = "jf477-ttl-device";
        var staleSession = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var freshSession = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        SessionReferenceCache.StoreAtForTests(
            user.JellyfinToken!, deviceId, staleSession, DateTimeOffset.UtcNow.AddSeconds(-61));
        SetupLookupReturns(freshSession);
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        SkillResponse response = await handler.HandleRequestAsync(
            CreateIntentRequest(), CreateContext(user, deviceId), CancellationToken.None);

        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Once);
        Assert.Single(handler.ReceivedSessions);
        Assert.Same(freshSession, handler.ReceivedSessions[0]);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    // A null lookup result must NOT be cached: the next request retries the lookup.
    [Fact]
    public async Task NullSessionResult_IsNotCached()
    {
        var user = CreateUser("jf477-null-token");
        const string deviceId = "jf477-null-device";
        _sessionManagerMock
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()))
            .ReturnsAsync((SessionInfo?)null);
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateIntentRequest();

        await handler.HandleRequestAsync(request, CreateContext(user, deviceId), CancellationToken.None);
        await handler.HandleRequestAsync(request, CreateContext(user, deviceId), CancellationToken.None);

        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Exactly(2));
    }

    // (c) PlaybackStarted warms the cache (TTL refresh) with no request-path lookup
    // beyond the event's own resolution; the next intent request is a pure cache hit.
    [Fact]
    public async Task PlaybackStarted_WarmsCache_NextIntentIsPureCacheHit()
    {
        var user = CreateUser("jf477-warm-token");
        const string deviceId = "jf477-warm-device";
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.UserId = user.Id; // the real lookup stamps the token owner onto the session
        SetupLookupReturns(session);
        var startedHandler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var probeHandler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var startedRequest = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0,
            RequestId = "started-jf477"
        };

        SkillResponse startedResponse = await startedHandler.HandleRequestAsync(
            startedRequest, CreateContext(user, deviceId), CancellationToken.None);

        // Event response stays a keep-alive ack (no AudioPlayer.Play, session not ended).
        Assert.Null(startedResponse.Response.ShouldEndSession);

        // The event's own resolution is the ONLY lookup; the warm Store adds none.
        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Once);
        Assert.True(SessionReferenceCache.TryGet(user.JellyfinToken, deviceId, out SessionInfo? warmed));
        Assert.Same(session, warmed);

        // The follow-up intent request is served entirely from the warmed cache.
        await probeHandler.HandleRequestAsync(CreateIntentRequest(), CreateContext(user, deviceId), CancellationToken.None);
        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Once);
        Assert.Same(session, probeHandler.ReceivedSessions.Single());
    }

    // (d) Dead session: OnPlaybackStart throwing ResourceNotFoundException (the session
    // is gone from the SessionManager) invalidates the device's entries, so the next
    // request refetches instead of reusing the corpse.
    [Fact]
    public async Task PlaybackStarted_DeadSession_InvalidatesCache_NextRequestRefetches()
    {
        var user = CreateUser("jf477-dead-token");
        const string deviceId = "jf477-dead-device";
        var corpse = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var fresh = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        SetupLookupReturns(corpse, fresh);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .ThrowsAsync(new ResourceNotFoundException("Session " + corpse.Id + " not found."));
        var startedHandler = new PlaybackStartedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var probeHandler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var startedRequest = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0,
            RequestId = "started-dead-jf477"
        };

        // The event resolves the (dying) session and caches it; the fire-and-forget
        // start report then finds it dead and invalidates the device's entries.
        await startedHandler.HandleRequestAsync(startedRequest, CreateContext(user, deviceId), CancellationToken.None);
        Assert.True(await TestHelpers.WaitUntilAsync(
            () => !SessionReferenceCache.TryGet(user.JellyfinToken, deviceId, out _),
            TimeSpan.FromSeconds(5)),
            "dead-session report should have invalidated the cache entry");

        // Next request refetches (second lookup) instead of reusing the corpse.
        await probeHandler.HandleRequestAsync(CreateIntentRequest(), CreateContext(user, deviceId), CancellationToken.None);
        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Exactly(2));
        Assert.Same(fresh, probeHandler.ReceivedSessions.Single());
    }

    // The stop-report path shares the dead-session invalidation: OnPlaybackStopped
    // throwing ResourceNotFoundException drops the device's entries (all tokens) while
    // other devices stay cached; the exception itself still propagates.
    [Fact]
    public async Task PlaybackStoppedReport_DeadSession_InvalidatesDeviceEntriesOnly()
    {
        const string deviceId = "jf477-stop-device";
        const string otherDeviceId = "jf477-stop-other-device";
        var sessionA = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var sessionB = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var sessionOther = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        SessionReferenceCache.Store("jf477-stop-token-a", deviceId, sessionA);
        SessionReferenceCache.Store("jf477-stop-token-b", deviceId, sessionB);
        SessionReferenceCache.Store("jf477-stop-token-other", otherDeviceId, sessionOther);
        _sessionManagerMock
            .Setup(s => s.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .ThrowsAsync(new ResourceNotFoundException("Session not found."));
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var stopInfo = new PlaybackStopInfo { SessionId = sessionA.Id, ItemId = Guid.NewGuid() };

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => handler.CallReportStopOrderedAsync(deviceId, Guid.NewGuid().ToString(), stopInfo));

        Assert.False(SessionReferenceCache.TryGet("jf477-stop-token-a", deviceId, out _));
        Assert.False(SessionReferenceCache.TryGet("jf477-stop-token-b", deviceId, out _));
        Assert.True(SessionReferenceCache.TryGet("jf477-stop-token-other", otherDeviceId, out SessionInfo? survivor));
        Assert.Same(sessionOther, survivor);
    }

    // (e) Reduced budget: a hanging first lookup fails within the 2s fast-fail budget
    // with the coherent not-found tell (wall-clock bound is loose: 4s), and the
    // abandoned lookup warm-fills the cache when it eventually lands.
    [Fact]
    public async Task HangingLookup_FastFailsWithinBudget_ThenWarmFills()
    {
        var user = CreateUser("jf477-hang-token");
        const string deviceId = "jf477-hang-device";
        var lateSession = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var neverCompletes = new TaskCompletionSource<SessionInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionManagerMock
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, deviceId, It.IsAny<string>()))
            .Returns(neverCompletes.Task);
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var stopwatch = Stopwatch.StartNew();
        SkillResponse response = await handler.HandleRequestAsync(
            CreateIntentRequest(), CreateContext(user, deviceId), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 4000,
            $"fast-fail budget not honored: {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(
            ResponseStrings.Get("UserNotFound", "en-US"),
            TestHelpers.GetSpeechText(response));
        Assert.Empty(handler.ReceivedSessions);

        // The abandoned lookup keeps running; when it lands, the cache is warm-filled.
        neverCompletes.SetResult(lateSession);
        Assert.True(await TestHelpers.WaitUntilAsync(
            () => SessionReferenceCache.TryGet(user.JellyfinToken, deviceId, out SessionInfo? warmed) && ReferenceEquals(warmed, lateSession),
            TimeSpan.FromSeconds(5)),
            "late lookup should warm-fill the cache");
    }

    // Shared Echo, two voice profiles alternating inside the TTL: Jellyfin keeps ONE
    // session object per device and re-stamps its UserId with the last resolver, so the
    // cached entry for profile A now names profile B. The hit guard must treat that as
    // a miss and refetch (re-stamping to A) instead of attributing A's request to B.
    [Fact]
    public async Task SharedDevice_ProfileSwitchWithinTtl_RefetchesInsteadOfServingOtherProfile()
    {
        var userA = CreateUser("jf477-profile-token-a");
        var userB = CreateUser("jf477-profile-token-b");
        const string deviceId = "jf477-profile-device";
        var shared = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        _sessionManagerMock
            .Setup(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), deviceId, It.IsAny<string>()))
            .ReturnsAsync((string token, string _, string _) =>
            {
                // Mirror the Jellyfin side effect: LogSessionActivity stamps the shared
                // per-device session with the LAST resolver's user id.
                shared.UserId = token == userA.JellyfinToken ? userA.Id : userB.Id;
                return shared;
            });
        var handler = new SessionRecordingHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = CreateIntentRequest();

        // A then B: two distinct cache keys, both misses (fresh lookups, re-stamping B).
        await handler.HandleRequestAsync(request, CreateContext(userA, deviceId), CancellationToken.None);
        await handler.HandleRequestAsync(request, CreateContext(userB, deviceId), CancellationToken.None);

        // A again within the TTL: the cached object names B, so the guard must miss and
        // the lookup must re-run, restoring A's attribution.
        await handler.HandleRequestAsync(request, CreateContext(userA, deviceId), CancellationToken.None);

        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(userA.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Exactly(2));
        _sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken(userB.JellyfinToken, deviceId, It.IsAny<string>()),
            Times.Once);
        Assert.Equal(3, handler.ReceivedSessions.Count);
        Assert.Equal(userA.Id, handler.ReceivedSessions[2].UserId);
    }

    // (f) Different devices and tokens never collide; InvalidateDevice clears only the
    // targeted device across tokens.
    [Fact]
    public void Cache_DistinctTokensAndDevices_DoNotCollide()
    {
        var sessionA = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var sessionB = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var sessionC = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        SessionReferenceCache.Store("token-1", "device-1", sessionA);
        SessionReferenceCache.Store("token-1", "device-2", sessionB);
        SessionReferenceCache.Store("token-2", "device-1", sessionC);

        Assert.True(SessionReferenceCache.TryGet("token-1", "device-1", out SessionInfo? a));
        Assert.Same(sessionA, a);
        Assert.True(SessionReferenceCache.TryGet("token-1", "device-2", out SessionInfo? b));
        Assert.Same(sessionB, b);
        Assert.True(SessionReferenceCache.TryGet("token-2", "device-1", out SessionInfo? c));
        Assert.Same(sessionC, c);
        Assert.False(SessionReferenceCache.TryGet("token-2", "device-2", out _));

        SessionReferenceCache.InvalidateDevice("device-1");
        Assert.False(SessionReferenceCache.TryGet("token-1", "device-1", out _));
        Assert.False(SessionReferenceCache.TryGet("token-2", "device-1", out _));
        Assert.True(SessionReferenceCache.TryGet("token-1", "device-2", out _));
    }
}
