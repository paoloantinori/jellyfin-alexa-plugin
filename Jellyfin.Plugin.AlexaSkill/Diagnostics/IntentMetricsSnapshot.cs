using System;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Immutable snapshot of per-intent metrics.
/// </summary>
public class IntentMetricsSnapshot
{
    public long Count { get; init; }
    public long ErrorCount { get; init; }
    public double TotalMs { get; init; }
    public double AverageMs { get; init; }
    public double MinMs { get; init; }
    public double MaxMs { get; init; }
    public DateTime? LastErrorAt { get; init; }
}
