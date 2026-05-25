using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// In-memory store for queue continuation state, keyed by user ID and device ID.
/// Follows the same pattern as <see cref="RadioModeState"/>.
/// </summary>
internal static class QueueContinuationStore
{
    private static readonly ConcurrentDictionary<string, QueueContinuation> _store = new();

    private static string Key(Guid userId, string deviceId)
        => $"{userId}:{deviceId}";

    /// <summary>
    /// Store continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="continuation">The continuation data to store.</param>
    public static void Set(Guid userId, string deviceId, QueueContinuation continuation)
        => _store[Key(userId, deviceId)] = continuation;

    /// <summary>
    /// Retrieve continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The continuation data, or null if none exists.</returns>
    public static QueueContinuation? Get(Guid userId, string deviceId)
        => _store.TryGetValue(Key(userId, deviceId), out var continuation) ? continuation : null;

    /// <summary>
    /// Remove continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static void Remove(Guid userId, string deviceId)
        => _store.TryRemove(Key(userId, deviceId), out _);

    /// <summary>
    /// Remove all entries for a given user (cleanup on session end).
    /// </summary>
    /// <param name="userId">The user ID whose entries should be removed.</param>
    public static void RemoveAllForUser(Guid userId)
    {
        string prefix = $"{userId}:";
        foreach (var kvp in _store)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _store.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Clear all entries. For test teardown only.
    /// </summary>
    internal static void Clear() => _store.Clear();
}
