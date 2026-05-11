using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Circuit breaker that tracks consecutive Jellyfin API failures per server URL.
/// Prevents wasting the 8-second Alexa timeout on doomed retries when the backend is down.
/// </summary>
public class CircuitBreaker
{
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="failureThreshold">Consecutive failures to trigger OPEN (default 5).</param>
    /// <param name="openDurationSeconds">Seconds in OPEN before HALF_OPEN (default 30).</param>
    /// <param name="failureWindowSeconds">Seconds within which failures must occur (default 60).</param>
    public CircuitBreaker(int failureThreshold = 5, int openDurationSeconds = 30, int failureWindowSeconds = 60)
    {
        FailureThreshold = failureThreshold;
        OpenDurationSeconds = openDurationSeconds;
        FailureWindowSeconds = failureWindowSeconds;
    }

    /// <summary>
    /// Gets the number of consecutive failures required to open the circuit.
    /// </summary>
    public int FailureThreshold { get; }

    /// <summary>
    /// Gets the duration in seconds the circuit stays OPEN before transitioning to HALF_OPEN.
    /// </summary>
    public int OpenDurationSeconds { get; }

    /// <summary>
    /// Gets the time window in seconds within which failures must occur to open the circuit.
    /// </summary>
    public int FailureWindowSeconds { get; }

    /// <summary>
    /// Check whether a request to the given server URL is allowed.
    /// </summary>
    /// <param name="serverUrl">The Jellyfin server URL.</param>
    /// <returns>True if the request should proceed, false if the circuit is OPEN.</returns>
    public bool IsRequestAllowed(string serverUrl)
    {
        var state = _circuits.GetOrAdd(serverUrl, _ => new CircuitState());

        lock (state)
        {
            switch (state.Status)
            {
                case CircuitStatus.Closed:
                    return true;

                case CircuitStatus.Open:
                    if (DateTimeOffset.UtcNow >= state.OpenedAt.AddSeconds(OpenDurationSeconds))
                    {
                        state.Status = CircuitStatus.HalfOpen;
                        return true; // allow one probe request
                    }

                    return false;

                case CircuitStatus.HalfOpen:
                    return false; // already allowing one probe, block additional

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Record a successful API call. Resets the circuit to CLOSED.
    /// </summary>
    /// <param name="serverUrl">The Jellyfin server URL.</param>
    public void RecordSuccess(string serverUrl)
    {
        var state = _circuits.GetOrAdd(serverUrl, _ => new CircuitState());

        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.FirstFailureAt = null;
            state.Status = CircuitStatus.Closed;
        }
    }

    /// <summary>
    /// Record a failed API call. May transition to OPEN if threshold is reached.
    /// </summary>
    /// <param name="serverUrl">The Jellyfin server URL.</param>
    /// <param name="logger">Optional logger for state transitions.</param>
    public void RecordFailure(string serverUrl, ILogger? logger = null)
    {
        var state = _circuits.GetOrAdd(serverUrl, _ => new CircuitState());

        lock (state)
        {
            var now = DateTimeOffset.UtcNow;

            // Reset if outside the failure window
            if (state.FirstFailureAt.HasValue && now > state.FirstFailureAt.Value.AddSeconds(FailureWindowSeconds))
            {
                state.ConsecutiveFailures = 0;
                state.FirstFailureAt = null;
            }

            state.FirstFailureAt ??= now;
            state.ConsecutiveFailures++;

            if (state.Status == CircuitStatus.HalfOpen)
            {
                // Probe failed — back to OPEN
                state.Status = CircuitStatus.Open;
                state.OpenedAt = now;
                logger?.LogWarning("Circuit breaker HALF_OPEN → OPEN for {ServerUrl} (probe failed)", serverUrl);
            }
            else if (state.ConsecutiveFailures >= FailureThreshold && state.Status == CircuitStatus.Closed)
            {
                state.Status = CircuitStatus.Open;
                state.OpenedAt = now;
                logger?.LogWarning(
                    "Circuit breaker CLOSED → OPEN for {ServerUrl} ({Failures} consecutive failures in {Window}s)",
                    serverUrl,
                    state.ConsecutiveFailures,
                    FailureWindowSeconds);
            }
        }
    }

    /// <summary>
    /// Get the current circuit status for a server URL (for diagnostics/testing).
    /// </summary>
    /// <param name="serverUrl">The Jellyfin server URL.</param>
    /// <returns>The current circuit status.</returns>
    public CircuitStatus GetStatus(string serverUrl)
    {
        if (_circuits.TryGetValue(serverUrl, out var state))
        {
            lock (state)
            {
                return state.Status;
            }
        }

        return CircuitStatus.Closed;
    }

    /// <summary>
    /// Reset all circuits (for testing).
    /// </summary>
    public void Reset()
    {
        _circuits.Clear();
    }

    private sealed class CircuitState
    {
        public CircuitStatus Status { get; set; } = CircuitStatus.Closed;

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? FirstFailureAt { get; set; }

        public DateTimeOffset OpenedAt { get; set; }
    }
}
