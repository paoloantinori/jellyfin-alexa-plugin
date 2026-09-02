#nullable enable
using System;
using System.Collections.Concurrent;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Per-device ordering guard for Jellyfin session playback reports (JF-425).
/// The PlaybackStarted report runs fire-and-forget off the Alexa response path
/// (JF-410: awaiting it inside the response breached the ~8s window), which removed
/// start-vs-stop ordering. The report's session write lands INSIDE
/// ISessionManager.OnPlaybackStart after an internal await (GetMediaSource in
/// SessionManager.UpdateNowPlayingItem, Jellyfin 10.11), so a stalled report can
/// complete and write Playing + NowPlayingItem long after a later stop already
/// cleared the session, resurrecting a zombie (position card, MediaInfo, dashboard
/// and resume fallback all show the dead item, and StartAutomaticProgress keeps it
/// ticking). The plugin cannot intercept that host-internal write, so the guard
/// restores the invariant instead: each device holds ONE slot with the stop that
/// supersedes any still in-flight start report, and a start report that completes
/// with a stop in the slot re-issues it, which is the host call that clears the
/// session (OnPlaybackStopped removes the now-playing item and stops the
/// automatic-progress timer).
/// Which events write the slot:
/// - PlaybackStarted (BeginStart): clears the slot; each start is a new generation
///   whose own report will populate the session.
/// - PlaybackStopped when NOT a displacement (RecordStop): a real user stop/pause;
///   the stop becomes the corrective action for any still in-flight start report.
/// - PlaybackFinished and PlaybackFailed when NOT a displacement (RecordStop): the
///   current item ended or failed; same corrective obligation as a user stop.
/// Deliberately NOT registered: displacement stops (the old track's stop, finish, or
/// failure while the new track owns the session; registering would let a late
/// old-start report replay that stop and clobber the new track's now-playing entry)
/// and OnPlaybackProgress
/// writers such as the loop-mode toggles (they are awaited in their own request
/// path, so a later stop cannot overtake them mid-write).
/// Design note: an explicit monotonic epoch compared against the report's
/// generation was evaluated first and collapses to this slot. For every reachable
/// interleaving the two produce identical outcomes, because the slot is non-null
/// exactly while the device's latest report is the stop the epoch would have
/// matched; the slot alone keeps that invariant with no counter to thread through.
/// Boundaries: start-vs-start ordering is not corrected (a start report is never
/// re-issued; that would double count PlayCount), so a late older start can
/// transiently show the old item until the host's automatic progress timer or the
/// next report overwrites it, and two stale reports both re-issue the pending stop
/// (idempotent). State is one entry per device, retained for the process lifetime
/// (bounded, same trade-off as NextTrackPrecomputeCache).
/// </summary>
internal static class PlaybackReportOrdering
{
    private static readonly ConcurrentDictionary<string, PlaybackStopInfo?> SupersedingStops = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a new playback generation for a device: clears any pending stop
    /// correction. Must be called BEFORE the start report is dispatched (the report's
    /// Task runs its synchronous prefix, including the ISessionManager.OnPlaybackStart
    /// call, inline in the handler).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (empty string shares one fallback slot).</param>
    internal static void BeginStart(string deviceId)
        => SupersedingStops[deviceId] = null;

    /// <summary>
    /// Records that a stop report is being sent for a device: from this instant, any
    /// in-flight start report for the device is superseded by this stop. Must be called
    /// BEFORE the OnPlaybackStopped call so that a start report completing while the
    /// stop is still in flight is already superseded.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (empty string shares one fallback slot).</param>
    /// <param name="stopInfo">The stop report being sent (replayed verbatim as the correction).</param>
    internal static void RecordStop(string deviceId, PlaybackStopInfo stopInfo)
        => SupersedingStops[deviceId] = stopInfo;

    /// <summary>
    /// Returns the stop report that supersedes a start report completing now, or null
    /// when the device's latest report is a start (its own write is authoritative).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The stop report to re-issue, or null when no correction is due.</returns>
    internal static PlaybackStopInfo? GetSupersedingStop(string deviceId)
        => SupersedingStops.TryGetValue(deviceId, out PlaybackStopInfo? stop) ? stop : null;

    /// <summary>
    /// Classifies a stop-shaped event token (PlaybackStopped/Finished/Failed) against
    /// the device queue (JF-425): true when the queue's current item exists and differs
    /// from the event token, meaning the event is the OLD stream ending as a newer play
    /// displaces it (the queue already moved on). Displacement events must NOT call
    /// <see cref="RecordStop"/>: registering one would let a late start report for the
    /// old item replay that stop and clear the NEW track's now-playing entry.
    /// </summary>
    /// <param name="queue">The device's queue, or null when the device has none (never a displacement).</param>
    /// <param name="token">The event's stream token (the item ID).</param>
    /// <param name="expectedItemId">Receives the queue's current item ID when one is set (for logging), null otherwise.</param>
    /// <returns>True when the event displaces an already-replaced stream.</returns>
    internal static bool IsDisplacementStop(DeviceQueue? queue, string? token, out string? expectedItemId)
    {
        expectedItemId = queue != null
            && queue.CurrentIndex >= 0
            && queue.CurrentIndex < queue.ItemIds.Count
            ? queue.ItemIds[queue.CurrentIndex]
            : null;

        return !string.IsNullOrEmpty(expectedItemId)
            && !string.Equals(expectedItemId, token, StringComparison.OrdinalIgnoreCase);
    }
}
