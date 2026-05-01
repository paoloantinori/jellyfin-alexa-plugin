using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Thread-safe request counter for tracking Alexa request metrics.
/// </summary>
public class RequestCounters
{
    private int _totalRequests;
    private int _totalErrors;

    /// <summary>
    /// Gets per-intent/request-type request counts.
    /// </summary>
    public ConcurrentDictionary<string, int> PerType { get; } = new();

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
    /// Gets the total number of requests processed.
    /// </summary>
    public int TotalRequests => Volatile.Read(ref _totalRequests);

    /// <summary>
    /// Gets the total number of errors encountered.
    /// </summary>
    public int TotalErrors => Volatile.Read(ref _totalErrors);
}
