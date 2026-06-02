using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.AlexaSkill.Configuration;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Tracks post-play state per device, keyed by userId:deviceId.
/// Set in PlaybackNearlyFinished when the queue is exhausted, consumed in
/// PlaybackFinished (AutoPlay/Ask) and Yes/No intent handlers (Ask).
/// Entries expire after 2 minutes to prevent stale state from interfering.
/// </summary>
internal static class PostPlayState
{
    private static readonly ConcurrentDictionary<string, Entry> _state = new();

    private static string Key(Guid userId, string deviceId)
        => $"{userId}:{deviceId}";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Set post-play state for a device. Called from PlaybackNearlyFinished
    /// when the queue is exhausted and PostPlayBehavior is AutoPlay or Ask.
    /// </summary>
    public static void Set(Guid userId, string deviceId, PostPlayBehavior mode, string itemId)
    {
        _state[Key(userId, deviceId)] = new Entry(mode, itemId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Try to get the post-play state for a device. Returns false if no state
    /// exists or the entry has expired (older than 2 minutes). Expired entries
    /// are removed automatically.
    /// </summary>
    public static bool TryGet(Guid userId, string deviceId, out PostPlayBehavior mode, out string? itemId)
    {
        if (_state.TryGetValue(Key(userId, deviceId), out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.Timestamp < Ttl)
            {
                mode = entry.Mode;
                itemId = entry.ItemId;
                return true;
            }

            _state.TryRemove(Key(userId, deviceId), out _);
        }

        mode = default;
        itemId = null;
        return false;
    }

    /// <summary>
    /// Remove post-play state for a device. Called after consuming the state
    /// in PlaybackFinished (AutoPlay) or Yes/No handlers (Ask).
    /// </summary>
    public static void Remove(Guid userId, string deviceId)
        => _state.TryRemove(Key(userId, deviceId), out _);

    /// <summary>
    /// Clear all entries. For test teardown only.
    /// </summary>
    internal static void Clear() => _state.Clear();

    private sealed record Entry(PostPlayBehavior Mode, string ItemId, DateTimeOffset Timestamp);
}
