using System;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class CircuitBreakerTests : PluginTestBase
{
    private readonly ILogger _logger;

    public CircuitBreakerTests()
    {
        _logger = LoggerFactory.Create(b => { }).CreateLogger<CircuitBreakerTests>();
    }

    // --- CLOSED state (normal operation) ---

    [Fact]
    public void IsRequestAllowed_Closed_ReturnsTrue()
    {
        var cb = new CircuitBreaker();
        Assert.True(cb.IsRequestAllowed("http://test:8096"));
    }

    [Fact]
    public void GetStatus_UnknownServer_ReturnsClosed()
    {
        var cb = new CircuitBreaker();
        Assert.Equal(CircuitStatus.Closed, cb.GetStatus("http://unknown:8096"));
    }

    // --- Transition CLOSED → OPEN ---

    [Fact]
    public void RecordFailure_ReachesThreshold_TransitionsToOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        cb.RecordFailure(url, _logger);
        Assert.True(cb.IsRequestAllowed(url)); // Still closed after 2 failures

        cb.RecordFailure(url, _logger);
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));
        Assert.False(cb.IsRequestAllowed(url));
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        cb.RecordFailure(url, _logger);
        cb.RecordSuccess(url); // resets
        cb.RecordFailure(url, _logger); // count is 1, not 3

        Assert.Equal(CircuitStatus.Closed, cb.GetStatus(url));
        Assert.True(cb.IsRequestAllowed(url));
    }

    // --- OPEN state behavior ---

    [Fact]
    public void IsRequestAllowed_Open_ReturnsFalse()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        Assert.False(cb.IsRequestAllowed(url));
    }

    // --- Transition OPEN → HALF_OPEN (time-based) ---

    [Fact]
    public void IsRequestAllowed_OpenAfterTimeout_TransitionsToHalfOpen_AndAllowsOne()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 0); // 0s = immediate half-open
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));

        // With 0s open duration, the next check should transition to HALF_OPEN and allow
        bool allowed = cb.IsRequestAllowed(url);
        Assert.True(allowed); // probe request allowed
    }

    // --- HALF_OPEN transitions ---

    [Fact]
    public void RecordSuccess_InHalfOpen_TransitionsToClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 0);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        cb.IsRequestAllowed(url); // triggers HALF_OPEN

        cb.RecordSuccess(url);
        Assert.Equal(CircuitStatus.Closed, cb.GetStatus(url));
        Assert.True(cb.IsRequestAllowed(url));
    }

    [Fact]
    public void RecordFailure_InHalfOpen_TransitionsBackToOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 60);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);

        // Manually simulate HALF_OPEN by manipulating timing via internal state
        // First, force to HALF_OPEN by getting status and setting openedAt in the past
        cb.IsRequestAllowed(url); // returns false (still OPEN, 60s not elapsed)
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));

        // Use Reset + manual approach: open with threshold, then force half-open via short duration
        cb.Reset();
        cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 0);
        cb.RecordFailure(url, _logger);
        cb.IsRequestAllowed(url); // triggers HALF_OPEN (0s open duration)
        Assert.Equal(CircuitStatus.HalfOpen, cb.GetStatus(url));

        cb.RecordFailure(url, _logger); // probe fails → back to OPEN
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));

        // With a fresh 0s timer, IsRequestAllowed would immediately re-transition to HALF_OPEN.
        // Verify the status is OPEN (it was set by RecordFailure) rather than testing IsRequestAllowed.
    }

    // --- Per-server isolation ---

    [Fact]
    public void RecordFailure_DifferentServers_AreIsolated()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        string url1 = "http://server1:8096";
        string url2 = "http://server2:8096";

        cb.RecordFailure(url1, _logger);
        Assert.False(cb.IsRequestAllowed(url1)); // server1 open
        Assert.True(cb.IsRequestAllowed(url2)); // server2 still closed
    }

    // --- Failure window ---

    [Fact]
    public void RecordFailure_OutsideWindow_ResetsCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, failureWindowSeconds: 1);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        cb.RecordFailure(url, _logger);

        // Wait for window to expire
        System.Threading.Thread.Sleep(1100);

        // Failures outside the window should reset
        cb.RecordFailure(url, _logger); // only 1 failure in new window
        Assert.Equal(CircuitStatus.Closed, cb.GetStatus(url));
    }

    // --- Reset ---

    [Fact]
    public void Reset_ClearsAllCircuits()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));

        cb.Reset();
        Assert.Equal(CircuitStatus.Closed, cb.GetStatus(url));
    }
}
