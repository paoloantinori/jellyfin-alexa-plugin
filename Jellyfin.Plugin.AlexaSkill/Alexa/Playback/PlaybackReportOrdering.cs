#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Per-device ordering guard for Jellyfin session playback reports (JF-425, extended
/// JF-447). The PlaybackStarted report runs fire-and-forget off the Alexa response path
/// (JF-410: awaiting it inside the response breached the ~8s window), which removed
/// start-vs-stop ordering. The report's session write lands INSIDE
/// ISessionManager.OnPlaybackStart after an internal await (GetMediaSource in
/// SessionManager.UpdateNowPlayingItem, Jellyfin 10.11), so a stalled report can
/// complete and write Playing + NowPlayingItem long after a later report already
/// changed the session. The plugin cannot intercept that host-internal write, so the
/// guard restores the invariant after the fact. Each device holds one state entry with:
/// - a monotonic start GENERATION (each PlaybackStarted increments it). A start report
///   captures its generation at dispatch and, once its own await completes, compares it
///   against the device's current generation to detect supersession.
/// - the PENDING STOP registration: the latest non-displacement stop whose correction
///   duty is still live. A superseded start report re-issues it (JF-425), and a newer
///   BeginStart clears it (that start owns the session now).
/// - the LAST START info, used to restore the current start's session entry after a
///   stale write clobbered it. Restoration is an AUTOMATED progress report, never a
///   re-issued start report: Jellyfin's OnPlaybackStart increments PlayCount per user
///   (verified in SessionManager at v10.11.11), so replaying a start would double
///   count plays and duplicate the PlaybackStart activity event.
/// Displacement classification (JF-447): a stop-shaped event whose token is an item
/// OTHER than the device's latest started item is the OLD stream ending as a newer
/// play displaces it. This reads NO DeviceQueueManager state: several play paths never
/// populate the device queue (PlaySongIntentHandler), which made the queue-based
/// classifier misjudge real stops as displacements and vice versa. The event order
/// alone is sufficient: a displacement stop arriving BEFORE the new start classifies
/// real, but the new start's BeginStart then clears the registration, so no stale
/// replay can clobber the new track either way.
/// Which events write the state:
/// - PlaybackStarted (BeginStart): clears the pending stop, records the start; each
///   start is a new generation whose own report will populate the session.
/// - PlaybackStopped/Finished/Failed when NOT a displacement (RecordStop): a real user
///   stop/pause/end/failure; the stop becomes the corrective action for any still
///   in-flight start report.
/// Deliberately NOT registered: displacement stops and OnPlaybackProgress writers
/// such as the loop-mode toggles (they are awaited in their own request path, so a
/// later stop cannot overtake them mid-write; see the LoopOn/LoopOff/LoopSongOn call
/// sites). Displacement stops still REPORT their stop to the server and then restore
/// the new start's session entry (the stop's own host-side write clears the
/// now-playing item and stops the automatic-progress timer even though the new track
/// is playing).
/// State is one entry per device, retained for the process lifetime (bounded, same
/// trade-off as NextTrackPrecomputeCache). The guard is best-effort over the
/// microscopic windows between the state reads: the reports it arbitrates stall for
/// seconds inside the host, so the residual interleavings (a generation advancing
/// between a corrector's check and its corrective call landing) are many orders of
/// magnitude rarer than the ones corrected here. Known open window (JF-447 review):
/// a displacement stop PROCESSED BEFORE the new item's PlaybackStarted classifies
/// real (the latest-start snapshot still names the old item), so its near-zero
/// offset can overwrite the old item's saved position. The stop path carries a
/// secondary queue-pointer contradiction check (PlaybackStoppedEventHandler) that
/// catches this when the maintained queue already advanced at directive time; play
/// paths that never populate the queue stay exposed to the overwrite.
/// </summary>
internal static class PlaybackReportOrdering
{
    private sealed class DeviceState
    {
        public long Generation;
        public volatile StopRegistration? PendingStop;
        public PlaybackStartInfo? LastStart;
    }

    private static readonly ConcurrentDictionary<string, DeviceState> Devices = new(StringComparer.Ordinal);

    /// <summary>
    /// A stop registered for correction duty. The recording handler owns the lifecycle:
    /// it calls <see cref="RecordStop"/> BEFORE sending its OnPlaybackStopped report and
    /// <see cref="MarkReportCompleted"/> after that report settles, so a correcting
    /// start report can wait for the original instead of firing a concurrent duplicate
    /// (duplicate SaveUserData transactions and activity entries, JF-447).
    /// </summary>
    internal sealed class StopRegistration
    {
        internal StopRegistration(PlaybackStopInfo info)
        {
            Info = info;
        }

        /// <summary>
        /// Gets the stop report to replay as the correction (verbatim).
        /// </summary>
        internal PlaybackStopInfo Info { get; }

        private readonly TaskCompletionSource _reportCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a task that completes when the recording handler's own OnPlaybackStopped
        /// report has settled (completed or failed).
        /// </summary>
        internal Task ReportCompleted => _reportCompleted.Task;

        /// <summary>
        /// Marks the recording handler's own stop report as settled. Safe to call from a
        /// finally block: the completion is per-registration, so a stop replaced in the
        /// device slot by a newer stop still completes its own registration.
        /// </summary>
        internal void MarkReportCompleted() => _reportCompleted.TrySetResult();
    }

    private static int _startReportsInFlight;
    private static int _correctionsWaitingOnInFlightStops;

    /// <summary>
    /// Gets a value indicating whether any fire-and-forget start report (including its
    /// ordering correction) is still running. Test seam: waiting on this is the
    /// deterministic replacement for sleep-based absence checks.
    /// </summary>
    internal static bool AnyStartReportsInFlight => Volatile.Read(ref _startReportsInFlight) > 0;

    /// <summary>
    /// Gets a value indicating whether any start-report correction is parked waiting
    /// for the recorded stop's own report to settle. Test seam for the double-stop
    /// interleaving: once observed, no second OnPlaybackStopped may be issued until the
    /// original completes.
    /// </summary>
    internal static bool AnyCorrectionsWaitingOnInFlightStops => Volatile.Read(ref _correctionsWaitingOnInFlightStops) > 0;

    /// <summary>
    /// Called by the start-report task at dispatch (pairs with
    /// <see cref="TrackStartReportSettled"/> in its finally block).
    /// </summary>
    internal static void TrackStartReportDispatched() => Interlocked.Increment(ref _startReportsInFlight);

    /// <summary>
    /// Called by the start-report task when it settles, correction included.
    /// </summary>
    internal static void TrackStartReportSettled() => Interlocked.Decrement(ref _startReportsInFlight);

    /// <summary>
    /// Called when a correction parks waiting for the recorded stop's own report.
    /// </summary>
    internal static void TrackCorrectionWaitBegun() => Interlocked.Increment(ref _correctionsWaitingOnInFlightStops);

    /// <summary>
    /// Called when a parked correction resumes.
    /// </summary>
    internal static void TrackCorrectionWaitEnded() => Interlocked.Decrement(ref _correctionsWaitingOnInFlightStops);

    /// <summary>
    /// Opens a new playback generation for a device: clears any pending stop correction
    /// and records the start for later restoration. Must be called BEFORE the start
    /// report is dispatched (the report's Task runs its synchronous prefix, including
    /// the ISessionManager.OnPlaybackStart call, inline in the handler). A start report
    /// whose item ID is <see cref="Guid.Empty"/> (unparseable token) leaves the
    /// displacement classification neutral instead of misclassifying every later stop.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (empty string shares one fallback slot).</param>
    /// <param name="startInfo">The start report being dispatched (the restoration source).</param>
    /// <returns>The generation this start report must match to treat its write as authoritative.</returns>
    internal static long BeginStart(string deviceId, PlaybackStartInfo startInfo)
    {
        DeviceState state = Devices.GetOrAdd(deviceId, _ => new DeviceState());
        state.LastStart = startInfo;
        state.PendingStop = null;
        return Interlocked.Increment(ref state.Generation);
    }

    /// <summary>
    /// Classifies a stop-shaped event token (PlaybackStopped/Finished/Failed) against
    /// the device's latest START (JF-447): true when the device's latest start is for a
    /// DIFFERENT item, meaning the event is the OLD stream ending as a newer play
    /// displaces it. Displacement events must NOT be registered for correction duty:
    /// registering one would let a late start report for the old item replay that stop
    /// and clear the NEW track's now-playing entry. Reads no queue state (several play
    /// paths never populate the device queue); with no started item recorded yet (or an
    /// empty one, from an unparseable token) the event is never a displacement
    /// (conservative: the plugin just restarted).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="token">The event's stream token (bare or composite).</param>
    /// <returns>True when the event displaces an already-replaced stream.</returns>
    internal static bool IsDisplacementStop(string deviceId, string? token)
    {
        return Devices.TryGetValue(deviceId, out DeviceState? state)
            && state.LastStart?.ItemId is { } startedItemId
            && startedItemId != Guid.Empty
            && StreamTokenCodec.TryGetItemId(token, out Guid stoppedItemId)
            && stoppedItemId != startedItemId;
    }

    /// <summary>
    /// Records that a stop report is being sent for a device: from this instant, any
    /// in-flight start report for the device is superseded by this stop. Must be called
    /// BEFORE the OnPlaybackStopped call so that a start report completing while the
    /// stop is still in flight is already superseded. Displacement stops are rejected
    /// structurally (the classification is folded in, not a caller-side protocol), so
    /// the returned registration is null exactly when the stop must not correct
    /// anything. The caller MUST call <see cref="StopRegistration.MarkReportCompleted"/>
    /// on the returned registration once its own report settles.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (empty string shares one fallback slot).</param>
    /// <param name="rawToken">The event's raw stream token, for the displacement classification.</param>
    /// <param name="stopInfo">The stop report being sent (replayed verbatim as the correction).</param>
    /// <returns>The registration owning the correction duty, or null for a displacement stop.</returns>
    internal static StopRegistration? RecordStop(string deviceId, string? rawToken, PlaybackStopInfo stopInfo)
    {
        if (IsDisplacementStop(deviceId, rawToken))
        {
            return null;
        }

        DeviceState state = Devices.GetOrAdd(deviceId, _ => new DeviceState());
        StopRegistration registration = new(stopInfo);
        state.PendingStop = registration;
        return registration;
    }

    /// <summary>
    /// Returns the stop registration currently holding the device's correction duty, or
    /// null when the latest report was a start (its own write is authoritative).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The pending stop registration, or null.</returns>
    internal static StopRegistration? GetPendingStop(string deviceId)
        => Devices.TryGetValue(deviceId, out DeviceState? state) ? state.PendingStop : null;

    /// <summary>
    /// Checks whether a registration still holds the device's correction duty (a newer
    /// stop may have replaced it, a newer start may have cleared it).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="registration">The registration previously obtained from <see cref="RecordStop"/> or <see cref="GetPendingStop"/>.</param>
    /// <returns>True when the registration is still the pending one.</returns>
    internal static bool IsPendingStop(string deviceId, StopRegistration registration)
        => Devices.TryGetValue(deviceId, out DeviceState? state)
            && ReferenceEquals(state.PendingStop, registration);

    /// <summary>
    /// Returns the device's current start generation (0 when the device is unknown).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The current generation.</returns>
    internal static long GetCurrentGeneration(string deviceId)
        => Devices.TryGetValue(deviceId, out DeviceState? state)
            ? Volatile.Read(ref state.Generation)
            : 0;

    /// <summary>
    /// Restores the device's latest start session entry via an AUTOMATED progress
    /// report. Used after a stale write clobbered it (start-vs-start, a corrective stop
    /// landing over a newer start, or a displacement stop's own write clearing the new
    /// track's entry). Automated reports rewrite the session's now-playing item and
    /// play state without touching per-user data (no PlayCount, no SaveUserData), which
    /// is why restoration never re-issues the start report itself. Best effort:
    /// failures are logged, never thrown.
    /// </summary>
    /// <param name="sessionManager">The session manager to report through.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="logger">The logger for the restore outcome.</param>
    /// <param name="reason">Short reason stamped into the log line.</param>
    /// <returns>A task representing the restoration.</returns>
    internal static async Task RestoreCurrentStartAsync(ISessionManager sessionManager, string deviceId, ILogger logger, string reason)
    {
        PlaybackStartInfo? start = Devices.TryGetValue(deviceId, out DeviceState? state) ? state.LastStart : null;
        if (start is null || start.ItemId == Guid.Empty)
        {
            // No recorded start, or one from an unparseable token: an empty ItemId would
            // CLEAR the session's now-playing entry instead of restoring it.
            return;
        }

        try
        {
            PlaybackProgressInfo progress = new PlaybackProgressInfo
            {
                SessionId = start.SessionId,
                ItemId = start.ItemId,
                PositionTicks = start.PositionTicks,
                RepeatMode = start.RepeatMode,
                PlaybackOrder = start.PlaybackOrder,
                IsPaused = false
            };

            logger.LogWarning(
                "PlaybackReportOrdering: restoring now-playing item {ItemId} via an automated progress report ({Reason}, JF-447)",
                start.ItemId,
                reason);
            await sessionManager.OnPlaybackProgress(progress, true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PlaybackReportOrdering: failed to restore now-playing item {ItemId} ({Reason})", start.ItemId, reason);
        }
    }

    /// <summary>
    /// Clears all per-device state and resets the in-flight seam counters. For test
    /// teardown only (mirrors RadioModeState and QueueContinuationStore resets in
    /// PluginTestBase); without the counter reset a leftover in-flight report from a
    /// previous test keeps <see cref="AnyStartReportsInFlight"/> set and breaks the
    /// next test's settle wait.
    /// </summary>
    internal static void Clear()
    {
        Devices.Clear();
        Interlocked.Exchange(ref _startReportsInFlight, 0);
        Interlocked.Exchange(ref _correctionsWaitingOnInFlightStops, 0);
    }
}
