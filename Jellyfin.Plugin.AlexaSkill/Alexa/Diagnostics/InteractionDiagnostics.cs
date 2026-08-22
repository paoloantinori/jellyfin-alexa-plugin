#nullable enable
using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Diagnostics;

/// <summary>
/// Per-device interaction diagnostics (JF-393). Records when a play request was issued and
/// when playback actually started, so that later control intents (pause/stop/session-end)
/// can be logged with their elapsed time since playback start. This is the data collection
/// for JF-392 ('alexa stop' intermittent routing failure): by correlating the user's
/// on-device stop attempts with the plugin log, each attempt where the intent NEVER reached
/// the skill is a routing-failure instance with known (invocation mode, elapsed) context.
/// All logging is gated on the per-user DiagnosticInteractionLogging setting (global default
/// <see cref="PluginConfiguration.DefaultDiagnosticInteractionLogging"/>), so normal users
/// get no extra log noise.
/// </summary>
public static class InteractionDiagnostics
{
    private sealed record DiagMarker(
        string? LastPlayIntent,
        bool? LastPlaySessionNew,
        DateTimeOffset? LastPlayRequestAt,
        DateTimeOffset? PlaybackStartedAt);

    private static readonly ConcurrentDictionary<string, DiagMarker> Markers = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves whether diagnostic interaction logging is enabled for a user:
    /// per-user override first, then the global default.
    /// </summary>
    /// <param name="user">The plugin user (may be null; falls back to the global default).</param>
    /// <param name="config">The plugin configuration holding the global default.</param>
    /// <returns>True if diagnostic logging is enabled.</returns>
    public static bool IsEnabled(User? user, PluginConfiguration config)
        => user?.DiagnosticInteractionLogging ?? config.DefaultDiagnosticInteractionLogging;

    /// <summary>
    /// True when the intent is one that starts playback (any Play* intent). Used to
    /// distinguish "play request" entries (recorded as the new playback origin) from
    /// control/other requests (logged with elapsed-since-playback-start). Prefix match
    /// (not Contains) so non-play intents that merely mention "play" don't misclassify.
    /// </summary>
    /// <param name="intentName">The intent name (e.g. "PlaySongIntent").</param>
    public static bool IsPlayInitiatingIntent(string? intentName)
        => !string.IsNullOrEmpty(intentName) && intentName.StartsWith("Play", StringComparison.Ordinal);

    /// <summary>
    /// Records a play-initiating request as the (candidate) playback origin for a device.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="intentName">The play intent name.</param>
    /// <param name="sessionNew">Whether the request opened a new session (one-shot vs interactive turn).</param>
    public static void RecordPlayRequest(string deviceId, string intentName, bool sessionNew)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        Markers.AddOrUpdate(deviceId,
            _ => new DiagMarker(intentName, sessionNew, DateTimeOffset.UtcNow, null),
            (_, m) => m with { LastPlayIntent = intentName, LastPlaySessionNew = sessionNew, LastPlayRequestAt = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Records that playback started on a device (PlaybackStarted event).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static void RecordPlaybackStarted(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Markers.AddOrUpdate(deviceId,
            _ => new DiagMarker(null, null, null, now),
            (_, m) => m with { PlaybackStartedAt = now });
    }

    /// <summary>
    /// Records that playback stopped on a device. Clears the playback-start timestamp so
    /// later control intents are not attributed to a finished playback (the play-request
    /// origin is kept: the next PlaybackStarted overwrites it anyway, and a stop attempt
    /// right after PlaybackStopped is still interesting context). Update-only: a stop for
    /// an unknown device does not create a marker.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static void RecordPlaybackStopped(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        if (Markers.TryGetValue(deviceId, out DiagMarker? m))
        {
            Markers[deviceId] = m with { PlaybackStartedAt = null };
        }
    }

    /// <summary>
    /// Seconds elapsed since playback last started on the device, or null if no playback
    /// is recorded as started.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static double? SincePlaybackStarted(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !Markers.TryGetValue(deviceId, out DiagMarker? m) || m.PlaybackStartedAt is not { } started)
        {
            return null;
        }

        return (DateTimeOffset.UtcNow - started).TotalSeconds;
    }

    /// <summary>
    /// Seconds elapsed since the last play request on the device, or null if none recorded.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static double? SincePlayRequest(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !Markers.TryGetValue(deviceId, out DiagMarker? m) || m.LastPlayRequestAt is not { } requested)
        {
            return null;
        }

        return (DateTimeOffset.UtcNow - requested).TotalSeconds;
    }

    /// <summary>
    /// The intent name of the last play request recorded for the device (null if none).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static string? LastPlayIntent(string deviceId)
        => Markers.TryGetValue(deviceId, out DiagMarker? m) ? m.LastPlayIntent : null;

    /// <summary>
    /// Whether the last play request for the device opened a new session (one-shot
    /// invocation) or was a turn inside an open session (null if unknown).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static bool? LastPlaySessionNew(string deviceId)
        => Markers.TryGetValue(deviceId, out DiagMarker? m) ? m.LastPlaySessionNew : null;

    /// <summary>
    /// Clears all markers (unit tests only).
    /// </summary>
    public static void ClearAll() => Markers.Clear();
}
