using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.ProactiveEvents;

/// <summary>
/// In-memory rate limiter for proactive events.
/// Enforces per-user limits: 10 events/hour and 50 events/day.
/// </summary>
internal class ProactiveEventRateLimiter
{
    private readonly ConcurrentDictionary<string, UserEventLog> _userLogs = new();

    /// <summary>
    /// Maximum events allowed per user per hour.
    /// </summary>
    public const int MaxEventsPerHour = 10;

    /// <summary>
    /// Maximum events allowed per user per day.
    /// </summary>
    public const int MaxEventsPerDay = 50;

    /// <summary>
    /// Check whether a user can receive another proactive event.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>True if the user is within rate limits.</returns>
    public bool CanSend(string userId)
    {
        var log = _userLogs.GetOrAdd(userId, _ => new UserEventLog());
        var now = DateTimeOffset.UtcNow;

        int hourCount = log.Timestamps.Count(ts => now - ts < TimeSpan.FromHours(1));
        int dayCount = log.Timestamps.Count(ts => now - ts < TimeSpan.FromHours(24));

        return hourCount < MaxEventsPerHour && dayCount < MaxEventsPerDay;
    }

    /// <summary>
    /// Record that an event was sent to a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    public void RecordSend(string userId)
    {
        var log = _userLogs.GetOrAdd(userId, _ => new UserEventLog());
        log.Timestamps.Add(DateTimeOffset.UtcNow);
        PruneOldEntries(log);
    }

    /// <summary>
    /// Remove entries older than 24 hours to prevent unbounded growth.
    /// </summary>
    private static void PruneOldEntries(UserEventLog log)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        for (int i = log.Timestamps.Count - 1; i >= 0; i--)
        {
            if (log.Timestamps[i] < cutoff)
            {
                log.Timestamps.RemoveAt(i);
            }
        }
    }

    private sealed class UserEventLog
    {
        public List<DateTimeOffset> Timestamps { get; } = new();
    }
}
