using System;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Immutable snapshot of per-intent metrics.
/// </summary>
public class IntentMetricsSnapshot
{
    /// <summary>
    /// Gets the total number of recorded responses.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Gets the total number of errors.
    /// </summary>
    public long ErrorCount { get; init; }

    /// <summary>
    /// Gets the total response time in milliseconds.
    /// </summary>
    public double TotalMs { get; init; }

    /// <summary>
    /// Gets the average response time in milliseconds.
    /// </summary>
    public double AverageMs { get; init; }

    /// <summary>
    /// Gets the minimum response time in milliseconds.
    /// </summary>
    public double MinMs { get; init; }

    /// <summary>
    /// Gets the maximum response time in milliseconds.
    /// </summary>
    public double MaxMs { get; init; }

    /// <summary>
    /// Gets the timestamp of the last error, or null if no errors occurred.
    /// </summary>
    public DateTime? LastErrorAt { get; init; }
}
