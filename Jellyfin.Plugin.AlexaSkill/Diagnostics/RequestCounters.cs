using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Thread-safe request counter for tracking Alexa request metrics.
/// </summary>
public class RequestCounters
{
    private int _totalRequests;
    private int _totalErrors;
    private long _startedAt;

    public RequestCounters()
    {
        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// Gets per-intent/request-type request counts.
    /// </summary>
    public ConcurrentDictionary<string, int> PerType { get; } = new();

    /// <summary>
    /// Gets per-intent timing and error metrics.
    /// </summary>
    private ConcurrentDictionary<string, IntentMetrics> PerIntentMetrics { get; } = new();

    /// <summary>
    /// Increment total request count.
    /// </summary>
    public void IncrementRequests() => Interlocked.Increment(ref _totalRequests);

    /// <summary>
    /// Increment total error count.
    /// </summary>
    public void IncrementErrors() => Interlocked.Increment(ref _totalErrors);

    /// <summary>
    /// Increment count for a specific request type.
    /// </summary>
    /// <param name="requestType">The request type to track.</param>
    public void IncrementType(string requestType) => PerType.AddOrUpdate(requestType, 1, (_, v) => v + 1);

    /// <summary>
    /// Record response time for an intent.
    /// </summary>
    /// <param name="intent">The intent or request type name.</param>
    /// <param name="elapsedMs">Response time in milliseconds.</param>
    public void RecordResponseTime(string intent, double elapsedMs)
    {
        PerIntentMetrics.AddOrUpdate(
            intent,
            _ => new IntentMetrics(elapsedMs),
            (_, existing) => existing.RecordResponse(elapsedMs));
    }

    /// <summary>
    /// Record an error for a specific intent.
    /// </summary>
    /// <param name="intent">The intent or request type name.</param>
    public void IncrementIntentError(string intent)
    {
        PerIntentMetrics.AddOrUpdate(
            intent,
            _ => IntentMetrics.WithInitialError(),
            (_, existing) => existing.RecordError());
    }

    /// <summary>
    /// Gets the total number of requests processed.
    /// </summary>
    public int TotalRequests => Volatile.Read(ref _totalRequests);

    /// <summary>
    /// Gets the total number of errors encountered.
    /// </summary>
    public int TotalErrors => Volatile.Read(ref _totalErrors);

    /// <summary>
    /// Gets the plugin uptime as a TimeSpan.
    /// </summary>
    public TimeSpan Uptime => DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref _startedAt));

    /// <summary>
    /// Gets a snapshot of per-intent metrics.
    /// </summary>
    public IReadOnlyDictionary<string, IntentMetricsSnapshot> GetIntentMetrics()
    {
        return PerIntentMetrics.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Snapshot());
    }
}
