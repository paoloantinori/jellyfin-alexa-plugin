using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Tracks radio mode state per Jellyfin session.
/// Uses an in-memory concurrent dictionary keyed by device ID + user ID.
/// </summary>
internal static class RadioModeState
{
    private static readonly ConcurrentDictionary<string, bool> _state = new();

    private static string Key(Guid userId, string deviceId)
        => $"{userId}:{deviceId}";

    public static bool IsEnabled(Guid userId, string deviceId)
        => _state.TryGetValue(Key(userId, deviceId), out bool enabled) && enabled;

    public static void Enable(Guid userId, string deviceId)
        => _state[Key(userId, deviceId)] = true;

    public static void Disable(Guid userId, string deviceId)
        => _state.TryRemove(Key(userId, deviceId), out _);

    /// <summary>
    /// Remove all entries for a given user (cleanup on session end).
    /// </summary>
    public static void DisableAllForUser(Guid userId)
    {
        string prefix = $"{userId}:";
        foreach (var kvp in _state)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _state.TryRemove(kvp.Key, out _);
            }
        }
    }
}
