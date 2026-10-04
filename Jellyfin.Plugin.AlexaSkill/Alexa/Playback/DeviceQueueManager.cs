using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Manages independent playback queues per Echo device with persistence.
/// Queues are stored in a ConcurrentDictionary keyed by device ID and
/// persisted to individual JSON files in the plugin data directory.
/// Writes are debounced to avoid excessive I/O during rapid queue changes.
/// </summary>
public sealed class DeviceQueueManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, DeviceQueue> _queues = new(StringComparer.Ordinal);
    private readonly KeyedOneShotDebounce _debounce = new(DebounceInterval);

    /// <summary>
    /// JF-655: the per-device ACTIVE-AUDIO flag, the event-owned playback signal the
    /// re-launch paths gate on. Deliberately a manager-level store, NOT a
    /// <see cref="DeviceQueue"/> field: it must never reach the persisted queue
    /// files, because a restart-persisted active flag would read as truth on a fresh
    /// boot and re-launch stale audio (the exact bug class JF-655 closes). Being
    /// in-memory on the DI-singleton manager, a plugin/process start begins with
    /// every flag clear by construction; only a live <c>PlaybackStarted</c> event
    /// sets one again.
    /// DRIFT GUARD (review finding, accepted as a parallel store rather than derived
    /// from <see cref="Playback.PlaybackReportOrdering"/>'s LastStart/PendingStop):
    /// that state is static and token-keyed where tests need per-instance isolation,
    /// so the two live in parallel by design; a future TERMINAL-event path (a new
    /// stop-shaped AudioPlayer event consumer) must update BOTH stores (the clear
    /// here plus the ordering registration) or the flag and the ordering state will
    /// disagree about whether the device is playing.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _activeAudioPlaybackDevices = new(StringComparer.Ordinal);

    /// <summary>
    /// JF-522: the launch-scope maps are the first per-item stores with writers on TWO
    /// threads (the request pipeline records at directive time; the AudioPlayer event
    /// thread promotes at PlaybackStarted), so unlike the sibling stores their
    /// mutations and reads serialize on this lock (the JF-425/JF-447 interleaving
    /// class: an unsynchronized Dictionary write can throw inside an event handler
    /// before the keep-alive ack Amazon requires).
    /// </summary>
    private readonly object _launchScopeLock = new();
    private readonly string _dataDirectory;
    private readonly ILogger<DeviceQueueManager> _logger;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceQueueManager"/> class.
    /// Loads persisted queues from disk on startup.
    /// </summary>
    /// <param name="dataDirectory">The directory where queue state files are stored.</param>
    /// <param name="logger">Logger instance.</param>
    public DeviceQueueManager(string dataDirectory, ILogger<DeviceQueueManager> logger)
    {
        _dataDirectory = dataDirectory;
        _logger = logger;

        LoadAllFromDisk();
    }

    /// <summary>
    /// Gets the queue for a device, creating an empty one if none exists.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The device's queue.</returns>
    public DeviceQueue GetOrCreateQueue(string deviceId)
    {
        return _queues.GetOrAdd(deviceId, _ => new DeviceQueue());
    }

    /// <summary>
    /// Gets the queue for a device WITHOUT creating one. Returns null if the
    /// device has no registered queue. Use this for read-only lookups (e.g.
    /// resolving the next item) to avoid synthesizing empty queue entries on
    /// every playback tick for devices that never registered a queue.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The device's queue, or null if none exists.</returns>
    public DeviceQueue? GetQueue(string deviceId)
    {
        return _queues.TryGetValue(deviceId, out DeviceQueue? queue) ? queue : null;
    }

    /// <summary>
    /// JF-568: which directive route carried a recorded last play. The route, not
    /// the item KIND, is what distinguishes "an Episode the skill last launched via
    /// AudioPlayer" (the JF-507 audio-only transcode and the JF-589 audio-route
    /// episodes, where transport directives work) from "the same Episode launched
    /// via VideoApp" (where they do not).
    /// </summary>
    public enum LaunchRoute
    {
        /// <summary>An <c>AudioPlayer.Play</c> directive launched the stream.</summary>
        Audio,

        /// <summary>A <c>VideoApp.Launch</c> directive launched the stream.</summary>
        VideoApp
    }

    /// <summary>
    /// Records the last user-initiated play on a device. Called from the
    /// BuildAudioPlayerResponse chokepoint on every ReplaceAll play, so it captures
    /// all play paths (intent handlers, APL carousel taps, resume confirmations).
    /// This is the device-specific source of truth that survives VideoApp.Launch
    /// plays which do not update context.AudioPlayer.Token.
    /// JF-568: the launch route is recorded beside the item id (see
    /// <see cref="LaunchRoute"/>), and the short-circuit compares BOTH: the same
    /// item re-launched on the other route (a screenless-degrade play followed by a
    /// VideoApp play of the same item, or the BuildAudioPlayerResponse delegation
    /// that re-records inside BuildVideoAppAudioResponse) must update the route, not
    /// short-circuit on the unchanged item id.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID that was played.</param>
    /// <param name="launchRoute">The directive route that launched the item.</param>
    public void RecordLastPlayed(string deviceId, string itemId, LaunchRoute launchRoute)
    {
        DeviceQueue queue = GetOrCreateQueue(deviceId);
        string routeName = launchRoute.ToString();

        // Short-circuit when NOTHING changed: avoids timer churn and redundant
        // disk writes when the same item is replayed or re-issued on the same route.
        // The FRESHNESS STAMP still refreshes (JF-619 review): a relaunch is a real
        // "this is what the device is on now" event, and freezing the old stamp would
        // permanently lose arbitration to an older queue-pointer write.
        if (string.Equals(queue.LastPlayedItemId, itemId, StringComparison.Ordinal)
            && string.Equals(queue.LastPlayedLaunchRoute, routeName, StringComparison.Ordinal))
        {
            queue.LastPlayedWrittenAt = DateTime.UtcNow;
            SchedulePersistInternal(deviceId);
            return;
        }

        queue.LastPlayedItemId = itemId;
        queue.LastPlayedLaunchRoute = routeName;
        queue.LastPlayedWrittenAt = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Recorded last played for device {DeviceId}: item={ItemId}, route={Route}",
            deviceId, itemId, routeName);
    }

    /// <summary>
    /// Gets the last-played item ID for a device without creating a queue entry.
    /// Read-side counterpart to <see cref="RecordLastPlayed"/>. Returns null if the
    /// device has no recorded play.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The last-played item ID, or null if none recorded.</returns>
    public string? GetLastPlayedItemId(string deviceId)
        => GetLastPlayedSnapshot(deviceId).ItemId;

    /// <summary>
    /// JF-626: the ledger's item id and route as ONE snapshot, read off a single
    /// queue lookup. The arbitration readers (PlaybackLaunchBuilder.ResolvePlayingMedium
    /// and ResolveCurrentPlayingItem) pair the two values into one verdict, and two
    /// independent reads could straddle a concurrent write, so every id+route
    /// consumer reads this snapshot instead. HONEST BOUNDS (JF-626 review): the
    /// single lookup removes the torn-READ shape entirely, but the pair is still
    /// not atomic against the write side (RecordLastPlayed assigns the id and the
    /// route in sequence), so a request thread interleaving between those two
    /// assignments can still observe the old id with the new route; only an
    /// immutable ledger entry (assigned as one reference) would close that.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The recorded item id and route; (null, null) when the device has nothing recorded.</returns>
    public (string? ItemId, LaunchRoute? Route) GetLastPlayedSnapshot(string deviceId)
    {
        return _queues.TryGetValue(deviceId, out DeviceQueue? queue)
            ? (queue.LastPlayedItemId, ParseRoute(queue.LastPlayedLaunchRoute))
            : (null, null);
    }

    /// <summary>
    /// JF-691: record the enqueue as the device was told it: the AudioPlayer token
    /// the directive's <c>ExpectedPreviousToken</c> names, and the item the
    /// directive carries. The ONE writer is the
    /// <c>PlaybackLaunchBuilder.BuildAudioPlayerResponse</c> chokepoint; the reader
    /// and the rationale live on
    /// <c>PlaybackFinishedEventHandler.EnqueuedForThisBoundary</c> (see
    /// <see cref="DeviceQueue.LastEnqueueAfterToken"/>). Short-circuits when
    /// NOTHING changed (the RecordLastPlayed idiom): Amazon multi-fires
    /// NearlyFinished, and identical re-enqueues must not churn the persist timer.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (the device-store key).</param>
    /// <param name="afterToken">The stream token the enqueue was issued after.</param>
    /// <param name="nextItemId">The item ID the enqueued stream will play.</param>
    public void RecordEnqueue(string deviceId, string afterToken, string nextItemId)
    {
        DeviceQueue queue = GetOrCreateQueue(deviceId);
        if (string.Equals(queue.LastEnqueueAfterToken, afterToken, StringComparison.Ordinal)
            && string.Equals(queue.LastEnqueueNextItemId, nextItemId, StringComparison.Ordinal))
        {
            return;
        }

        queue.LastEnqueueAfterToken = afterToken;
        queue.LastEnqueueNextItemId = nextItemId;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Recorded enqueue for device {DeviceId}: afterToken={AfterToken}, nextItem={NextItemId}",
            deviceId, afterToken, nextItemId);
    }

    /// <summary>
    /// JF-655: marks the device as ACTIVELY playing AudioPlayer audio. Written by the
    /// <c>PlaybackStarted</c> event handler only: the Echo sending that event is the
    /// platform's own playback report, and no launch-site shortcut may set it (the
    /// e2e/simulated environment fires no events, so a directive-time setter would
    /// pin the flag on devices where nothing ever played).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void MarkAudioPlaybackStarted(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        _activeAudioPlaybackDevices[deviceId] = 1;
    }

    /// <summary>
    /// JF-655: clears the device's active-audio flag. Written by the
    /// Stopped/Finished/Failed event handlers for every NON-displacement terminal
    /// event: the stream that stopped is the one the flag names. DISPLACEMENT
    /// terminal events (a newer stream already started; the displaced old stream's
    /// late Stopped/Finished/Failed) must NOT clear, because the device is actively
    /// playing the newer stream (code-review finding: Started(new) can be processed
    /// before the displaced Stopped(old)).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void MarkAudioPlaybackStopped(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        _activeAudioPlaybackDevices.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// JF-655 read side: whether a <c>PlaybackStarted</c> is currently in effect for
    /// the device (no terminal event since). In-memory only: false on a fresh
    /// manager (plugin/process start) even for devices whose persisted ledger
    /// entries survive, which is the property the re-launch gates rely on.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>True when the device has an active AudioPlayer stream per its events.</returns>
    public bool IsAudioPlaybackActive(string deviceId)
        => !string.IsNullOrEmpty(deviceId) && _activeAudioPlaybackDevices.ContainsKey(deviceId);

    /// <summary>
    /// JF-619: the ONE device resume truth-source. The queue pointer
    /// (<see cref="DeviceQueue.CurrentItemId"/>, written by AudioPlayer stop events) and
    /// the last-played record (<see cref="DeviceQueue.LastPlayedItemId"/>, written at
    /// launch time by <c>RecordLastPlayed</c>, video-inclusive) used to be read by
    /// DIFFERENT entry points (bare ResumeIntent vs the LaunchRequest offer), so one
    /// device could offer two different resumes. The resolver picks the pointer with
    /// the FRESHER write stamp, with a grace window: a stop event for the song a voice
    /// request just paused can land seconds AFTER the launch it yielded to (the
    /// documented pause-on-voice-request shape), and within that window the
    /// LAUNCH-time record wins (launches express user intent; delayed stops are
    /// bookkeeping). Null stamps (pre-JF-619 files) keep the audio-biased queue
    /// pointer, matching the live semantics of "riprendi" after an interrupted song;
    /// UPGRADE-WINDOW TRANSIENT (review finding, accepted): an old file whose offer
    /// used to read the last-played record alone may name the audio item once after
    /// upgrade, until the first write to either store restores stamped arbitration.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The winning item id and which store it came from; (null, QueuePointer) when the device has nothing recorded.</returns>
    public (string? ItemId, DeviceResumeSource Source) GetDeviceResumePointer(string deviceId)
    {
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return (null, DeviceResumeSource.QueuePointer);
        }

        if (string.IsNullOrEmpty(queue.CurrentItemId))
        {
            return (queue.LastPlayedItemId, DeviceResumeSource.LastPlayed);
        }

        if (string.IsNullOrEmpty(queue.LastPlayedItemId))
        {
            return (queue.CurrentItemId, DeviceResumeSource.QueuePointer);
        }

        // Legacy files (either stamp null): the old tie rule, queue pointer wins.
        if (queue.CurrentItemWrittenAt is null || queue.LastPlayedWrittenAt is null)
        {
            return (queue.CurrentItemId, DeviceResumeSource.QueuePointer);
        }

        DateTime currentAt = queue.CurrentItemWrittenAt.Value;
        DateTime lastAt = queue.LastPlayedWrittenAt.Value;

        // Fresh stamps: newer wins, with the delayed-stop grace window tilted to the
        // launch-time record (a stop landing just after a newer launch is the pause
        // that voice request caused, not a fresher truth).
        return lastAt >= currentAt - LaunchVsStopGrace
            ? (queue.LastPlayedItemId, DeviceResumeSource.LastPlayed)
            : (queue.CurrentItemId, DeviceResumeSource.QueuePointer);
    }

    /// <summary>
    /// The delayed-stop grace window for <see cref="GetDeviceResumePointer"/>: a
    /// PlaybackStopped for the song a voice request paused routinely arrives seconds
    /// after the launch that request triggered, so a queue-pointer stamp inside this
    /// window after a launch-time record does not outrank it.
    /// </summary>
    private static readonly TimeSpan LaunchVsStopGrace = TimeSpan.FromSeconds(30);

    /// <summary>Which store a JF-619 device resume pointer came from.</summary>
    public enum DeviceResumeSource
    {
        /// <summary>The AudioPlayer stop-event queue pointer (audio-biased on ties).</summary>
        QueuePointer,

        /// <summary>The last-played record, written by launch-time recording (video-inclusive).</summary>
        LastPlayed,
    }

    /// <summary>The route-name parse shared by the snapshot read (null on legacy or unparsable names).</summary>
    private static LaunchRoute? ParseRoute(string? routeName)
        => Enum.TryParse<LaunchRoute>(routeName, out LaunchRoute route) ? route : null;

    /// <summary>
    /// The ONE writer family for a launch-scope base/rate pair (JF-637, hardening
    /// the JF-636 lockstep that comments previously enforced at every write site):
    /// a base map and its rate map are WRITTEN, removed at write time, and carried
    /// only in pairs, so a future single-map write cannot break the invariant "a
    /// rate entry exists iff its base entry exists" at the write sites. The
    /// lockstep is load-bearing: <see cref="RecordLaunchBase"/>'s active
    /// short-circuit checks only the BASE map for a stale pending entry, so an
    /// orphan rate written beside no base would silently survive it and later
    /// scale a new stream's offsets with the wrong rate. Two paths remain
    /// convention-enforced rather than structural (same as pre-JF-637): the trim
    /// (<see cref="TrimLaunchBaseIfNeeded"/>) evicts each map by its own
    /// insertion order, which can in principle orphan a rate half on legacy
    /// mixed-age files near the cap, and <see cref="CopySurvivingStores"/> must
    /// keep carrying all four maps; the structural fix is the single
    /// scope-per-key map the JF-637 review named as the follow-up. One tolerated
    /// legacy shape: a pre-JF-636 file can carry a base WITHOUT a rate
    /// (<see cref="PromotePendingLaunchBase"/> defaults those to 1000); the
    /// reverse direction must never exist.
    /// </summary>
    private static void WritePendingLaunchScope(DeviceQueue queue, string key, long baseMs, int ratePerMille)
    {
        queue.PendingLaunchBaseMs[key] = baseMs;
        queue.PendingPlaybackRatePerMille[key] = ratePerMille;
    }

    /// <summary>
    /// Active-map half of the paired-write family (see
    /// <see cref="WritePendingLaunchScope"/> for the lockstep invariant): writes
    /// the active base+rate pair. Pair it with
    /// <see cref="RetirePendingLaunchScope"/> when the write supersedes a
    /// still-pending enqueue (the <see cref="RecordLaunchBase"/> active branch);
    /// <see cref="PromotePendingLaunchBase"/> needs only this half, having
    /// already extracted the pending pair's values with its own removes.
    /// </summary>
    private static void WriteActiveLaunchScope(DeviceQueue queue, string key, long baseMs, int ratePerMille)
    {
        queue.ActiveLaunchBaseMs[key] = baseMs;
        queue.ActivePlaybackRatePerMille[key] = ratePerMille;
    }

    /// <summary>
    /// Pending-retire half of the paired-write family: removes the item's pending
    /// base+rate pair together (the new stream supersedes the enqueued one).
    /// </summary>
    private static void RetirePendingLaunchScope(DeviceQueue queue, string key)
    {
        queue.PendingLaunchBaseMs.Remove(key);
        queue.PendingPlaybackRatePerMille.Remove(key);
    }

    /// <summary>
    /// JF-522 launch-scope write: records the item-absolute base of the
    /// <c>AudioPlayer.Play</c> directive just issued for an item on a device. An
    /// ENQUEUED directive routes to <see cref="DeviceQueue.PendingLaunchBaseMs"/>
    /// (promoted to active at the item's next PlaybackStarted, keeping the running
    /// stream's active base intact for wrapped/repeat-one queues); every other
    /// behavior routes to <see cref="DeviceQueue.ActiveLaunchBaseMs"/> and retires
    /// any pending entry for the same item (the new stream supersedes it). Keys are
    /// normalized to "N" format, matching the writer/reader event handlers' codec
    /// parsing. Short-circuits on an unchanged value (the
    /// <see cref="RecordLastPlayed"/> shape) to avoid persist churn.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID the directive launches (any GUID format).</param>
    /// <param name="baseMs">The item-absolute launch base in milliseconds (the minted
    /// <c>?start=</c> on the transcode route, 0 on the raw-static route).</param>
    /// <param name="enqueued">True when the directive plays <c>Enqueue</c>/<c>ReplaceEnqueued</c>.</param>
    /// <param name="ratePerMille">The stream's playback rate in per-mille form (JF-636:
    /// 1000 = identity, the raw-static/transcode streams; 750..2000 = an atempo
    /// stream whose device offsets must scale before composing the base).</param>
    /// <remarks>JF-723: the entry this call writes is exempt from this call's own
    /// cap-pressure trim (see <see cref="TrimLaunchBaseIfNeeded"/>), because the
    /// launch build runs before the queue commit on the play paths and the trim
    /// would otherwise judge the fresh entry against the OLD queue's membership.</remarks>
    public void RecordLaunchBase(string deviceId, string itemId, long baseMs, bool enqueued, int ratePerMille = 1000)
    {
        if (!StreamTokenCodec.TryGetItemId(itemId, out Guid parsedItemId))
        {
            return;
        }

        string key = parsedItemId.ToString("N");
        lock (_launchScopeLock)
        {
            DeviceQueue queue = GetOrCreateQueue(deviceId);

            if (enqueued)
            {
                if (queue.PendingLaunchBaseMs.TryGetValue(key, out long existingPending) && existingPending == baseMs
                    && queue.PendingPlaybackRatePerMille.TryGetValue(key, out int existingPendingRate) && existingPendingRate == ratePerMille)
                {
                    return;
                }

                WritePendingLaunchScope(queue, key, baseMs, ratePerMille);
            }
            else
            {
                // The short-circuit considers the RATE too (JF-636): a speed change
                // re-launches from the same content position, so base and rate are
                // independent (base 0 at 1.5x, then base 0 at 2.0x); skipping on an
                // unchanged base alone would leave the stale rate composing the new
                // stream's offsets.
                if (queue.ActiveLaunchBaseMs.TryGetValue(key, out long existingActive) && existingActive == baseMs
                    && queue.ActivePlaybackRatePerMille.TryGetValue(key, out int existingRate) && existingRate == ratePerMille
                    && !queue.PendingLaunchBaseMs.ContainsKey(key))
                {
                    return;
                }

                WriteActiveLaunchScope(queue, key, baseMs, ratePerMille);
                RetirePendingLaunchScope(queue, key);
            }

            TrimLaunchBaseIfNeeded(queue, key);
        }

        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Recorded {Scope} launch base for device {DeviceId}: item={ItemId}, base={BaseMs}ms, rate={RatePerMille}/1000",
            enqueued ? "pending" : "active", deviceId, itemId, baseMs, ratePerMille);
    }

    /// <summary>
    /// JF-522: promotes an item's pending (enqueued) launch base to active, called
    /// when the item's stream starts. No-op without a pending entry (a ReplaceAll
    /// launch already wrote active at directive time; a pre-deploy enqueue left none,
    /// and the stale active entry of an older stream of the same item is then left to
    /// the runtime guard at the writers). Unparsable item IDs are ignored.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The started item ID (any GUID format; composite stream
    /// tokens must be codec-parsed by the caller first).</param>
    public void PromotePendingLaunchBase(string deviceId, string itemId)
    {
        if (!StreamTokenCodec.TryGetItemId(itemId, out Guid parsedItemId)
            || !_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return;
        }

        string key = parsedItemId.ToString("N");
        long baseMs;
        int ratePerMille;
        lock (_launchScopeLock)
        {
            if (!queue.PendingLaunchBaseMs.Remove(key, out baseMs))
            {
                return;
            }

            // JF-636: the rate rides with its base (an atempo stream enqueued into a
            // wrapped queue scales its offsets from the moment it starts). The
            // 1000 default is the tolerated pre-JF-636 legacy shape (pending base
            // persisted before the rate map existed); see WritePendingLaunchScope.
            ratePerMille = queue.PendingPlaybackRatePerMille.Remove(key, out int pendingRate)
                ? pendingRate
                : 1000;
            WriteActiveLaunchScope(queue, key, baseMs, ratePerMille);
        }

        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Promoted pending launch base to active for device {DeviceId}: item={ItemId}, base={BaseMs}ms, rate={RatePerMille}/1000",
            deviceId, itemId, baseMs, ratePerMille);
    }

    /// <summary>
    /// JF-522 read side: the ACTIVE launch base for an item on a device (the stream
    /// most recently started on it, or issued via ReplaceAll), without creating a
    /// queue entry. Null means no launch scope is recorded (pre-deploy launch, cleared
    /// queue file): callers treat null as base 0.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <returns>The active launch base in milliseconds, or null when none.</returns>
    public long? GetActiveLaunchBase(string deviceId, string itemId)
    {
        if (!StreamTokenCodec.TryGetItemId(itemId, out Guid parsedItemId)
            || !_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return null;
        }

        lock (_launchScopeLock)
        {
            return queue.ActiveLaunchBaseMs.TryGetValue(parsedItemId.ToString("N"), out long baseMs)
                ? baseMs
                : null;
        }
    }

    /// <summary>
    /// JF-636 rate half of the launch-scope read: the ACTIVE playback rate for an
    /// item on a device, the rate of the stream whose events must scale their raw
    /// device offsets. Null means no rate entry exists (a pre-JF-636 or identity
    /// stream); callers treat null and 1000 identically (no scaling).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <returns>The active playback rate in per-mille form, or null when none.</returns>
    public int? GetActivePlaybackRate(string deviceId, string itemId)
        => GetActiveLaunchScope(deviceId, itemId).RatePerMille;

    /// <summary>
    /// JF-636: BOTH halves of the launch scope in ONE lock acquisition. The base
    /// and its rate are written together under <c>_launchScopeLock</c>; reading
    /// them through the two individual getters can straddle a concurrent
    /// <see cref="RecordLaunchBase"/> and pair base1 with rate2, so every reader
    /// that uses both values (the event-side composition, the resume rebase)
    /// reads them through this combined snapshot instead.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <returns>The active launch base in milliseconds (null when none) and the active playback rate in per-mille form (null when none).</returns>
    public (long? BaseMs, int? RatePerMille) GetActiveLaunchScope(string deviceId, string itemId)
    {
        if (!StreamTokenCodec.TryGetItemId(itemId, out Guid parsedItemId)
            || !_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return (null, null);
        }

        string key = parsedItemId.ToString("N");
        lock (_launchScopeLock)
        {
            long? baseMs = queue.ActiveLaunchBaseMs.TryGetValue(key, out long b) ? b : null;
            int? rate = queue.ActivePlaybackRatePerMille.TryGetValue(key, out int r) ? r : null;
            return (baseMs, rate);
        }
    }

    /// <summary>
    /// JF-375 test/record seam: sets the queue's live now-playing pointer exactly
    /// as the PlaybackStopped event path does (CurrentItemId + CurrentPositionTicks).
    /// Follow-me reads this as its freshest cross-device position signal.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <param name="positionTicks">The playback position in ticks.</param>
    internal void RecordNowPlaying(string deviceId, string itemId, long positionTicks)
    {
        if (!Guid.TryParse(itemId, out Guid parsed))
        {
            return;
        }

        DeviceQueue queue = GetOrCreateQueue(deviceId);
        // The PRODUCTION pointer shape: PlaybackStoppedEventHandler stores the
        // dashed Guid.ToString() form, not "N" (review C1 - the read must stay
        // format-agnostic because this is what it sees in the wild).
        queue.SetCurrentItemPointer(parsed.ToString(), positionTicks);
    }

    /// <summary>
    /// JF-375 test/record seam: writes the durable per-item position store exactly
    /// as the PlaybackStopped event path does (ItemPositionState, "N"-keyed).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <param name="positionTicks">The playback position in ticks.</param>
    internal void RecordItemPosition(string deviceId, string itemId, long positionTicks)
    {
        if (!Guid.TryParse(itemId, out Guid parsed) || positionTicks <= 0)
        {
            return;
        }

        DeviceQueue queue = GetOrCreateQueue(deviceId);
        queue.ItemPositionState[parsed.ToString("N")] = positionTicks;
    }

    /// <summary>
    /// JF-581 read side: the stored per-item position (ItemPositionState, written
    /// unconditionally by the PlaybackStopped handler) for an item on a device,
    /// without creating a queue entry. Null when no positive position is recorded.
    /// The plugin-owned store this exposes is immune to the server-side UserData
    /// write loss the resume seeds must survive (live incident 2026-09-16).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID in any GUID format (normalized to "N").</param>
    /// <returns>The stored position in ticks, or null when none.</returns>
    public long? GetStoredPositionTicks(string deviceId, string itemId)
    {
        if (!Guid.TryParse(itemId, out Guid parsedItemId)
            || !_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return null;
        }

        return queue.ItemPositionState.TryGetValue(parsedItemId.ToString("N"), out long ticks) && ticks > 0
            ? ticks
            : null;
    }

    /// <summary>
    /// JF-581/JF-565: the ONE UserData-first, plugin-store-fallback resume-position
    /// resolution for a read-side seed. UserData is preferred (cross-client, survives
    /// plugin data loss); when it reads non-positive the plugin-owned
    /// <see cref="GetStoredPositionTicks"/> backs it up, because the live 2026-09-16
    /// incident proved the server-side UserData writes can be lost for Alexa sessions
    /// while this store (written unconditionally by the PlaybackStopped handler) held
    /// the real ticks. A PLAYED item is the exception: completion legitimately resets
    /// UserData to 0, and the store still holds an older mid-listen position that must
    /// not resurrect over the finished listen. Static (not instance) so a caller with
    /// no manager reference (cold plugin instance) resolves without a null dance; a
    /// null manager simply means no fallback arm.
    /// </summary>
    /// <param name="queueManager">The manager owning the per-item position store; null disables the fallback arm.</param>
    /// <param name="deviceId">The Alexa device ID the stored position is scoped to.</param>
    /// <param name="itemId">The item ID in any GUID format.</param>
    /// <param name="userDataTicks">The UserData playback position the caller already read (0 when unread).</param>
    /// <param name="userDataPlayed">Whether UserData marks the item Played.</param>
    /// <param name="logger">Optional logger; the seed fires an Information line (the server-side write-loss diagnostic).</param>
    /// <param name="logLabel">Caller identity for the seed log line.</param>
    /// <returns>The effective resume position in ticks (0 when nothing is stored).</returns>
    public static long ResolveResumeTicks(
        DeviceQueueManager? queueManager,
        string? deviceId,
        string itemId,
        long userDataTicks,
        bool userDataPlayed,
        ILogger? logger = null,
        string logLabel = "ResumeSeed")
    {
        if (userDataTicks > 0)
        {
            return userDataTicks;
        }

        if (userDataPlayed)
        {
            return 0;
        }

        long? storedTicks = queueManager?.GetStoredPositionTicks(deviceId ?? string.Empty, itemId);
        if (storedTicks != null)
        {
            logger?.LogInformation(
                "{Label}: item {ItemId} UserData position was 0 (server-side write loss, JF-581); seeded from ItemPositionState: {Ticks} ticks",
                logLabel, itemId, storedTicks.Value);
            return storedTicks.Value;
        }

        return 0;
    }

    /// <summary>
    /// Bounds the four launch-scope dictionaries (the base/rate map family)
    /// exactly like the sibling trims (JF-514/JF-522): over the cap, remove the
    /// oldest entries whose item is not in the current queue; entries for queued
    /// items all stay. RESIDUAL (pre-existing, JF-637 review): each map is evicted
    /// by its own insertion order, so a base/rate pair whose halves entered at
    /// different times (a pre-JF-636 base with a later rate) can be split near
    /// the cap; harmless while promotion defaults a missing rate to 1000 and the
    /// writers keep every NEW pair inserted together. The structural fix is the
    /// single scope-per-key map named in <see cref="WritePendingLaunchScope"/>.
    /// The JF-723 exemption and the JF-738 membership normalization are
    /// documented on the trim method itself (<see cref="TrimLaunchBaseIfNeeded"/>).
    /// Internal for the InternalsVisibleTo test seam (the VideoAudioCache /
    /// KeyedOneShotDebounce pattern): the JF-723 pins seed their cap pressure from
    /// this constant so a cap change cannot silently degrade them to vacuous green.
    /// </summary>
    internal const int MaxLaunchBaseEntries = 200;

    /// <summary>
    /// JF-723 (derive-to-commit window): the entry <see cref="RecordLaunchBase"/>
    /// just wrote is EXEMPT from this trim via <paramref name="freshlyRecordedKey"/>
    /// (treated as queued by THIS call's trim only). The play paths build the
    /// launch BEFORE committing the queue (the JF-687/JF-699/JF-713
    /// refusal-before-phantom-state ordering), so this trim runs while the OLD
    /// queue is still the stored one and the freshly launched item (on the
    /// playlist arms, an item of the queue that is ABOUT to be committed) is not
    /// in the membership the trim judges against. Without the exemption, a
    /// cap-pressure trim could evict the fresh entry mid-window, and its absence
    /// reads as base 0 / rate identity at every reader (a silent rate-identity on
    /// a nonzero-base launch). The exemption can leave a map at cap+1 until the
    /// next launch ages the entry (the same over-cap tolerance
    /// <see cref="TrimPositionMap"/> already grants queued-pinned maps); the entry
    /// is NOT immortal: the NEXT launch's trim evicts it if it is still neither
    /// queued nor fresh. SCOPE OF THE EXEMPTION (JF-739, filed not fixed here):
    /// it guards the entry against its OWN record's trim only; a SIBLING
    /// <see cref="RecordLaunchBase"/> interleaved inside the same derive-to-commit
    /// window (a PlaybackNearlyFinished enqueue or queue-editing launch between
    /// the build and the commit) trims without exempting the earlier entry, whose
    /// item is still absent from the STORED queue, so that sibling trim can evict
    /// it; the JF-723 filing's fresh-stamping candidate (b) is JF-739's fix shape.
    /// MEMBERSHIP (JF-738, fixed here): the queued set is built through
    /// <see cref="NormalizeToMapKeyFormat"/>, so the stored queue's DASHED ids do
    /// protect the maps' "N"-keyed entries; before JF-738 the set was a raw
    /// ItemIds copy, never matched a key, and this trim ran as pure
    /// insertion/slot-order FIFO (which is how a fresh insert could be its own
    /// evictee at saturation); the exemption above held under BOTH behaviors by
    /// construction. JF-739 INTERPLAY: restored membership does NOT close the
    /// sibling-trim hole, because membership protects only items already in the
    /// STORED queue; inside the derive-to-commit window the fresh item is still
    /// absent from it, so a sibling trim (or this trim, without the exemption)
    /// still judges the entry non-queued.
    /// </summary>
    /// <param name="queue">The device queue whose four launch-scope maps are bounded.</param>
    /// <param name="freshlyRecordedKey">The "N"-normalized key RecordLaunchBase just
    /// wrote; never this trim's evictee (the JF-723 guard).</param>
    private static void TrimLaunchBaseIfNeeded(DeviceQueue queue, string freshlyRecordedKey)
    {
        if (queue.ActiveLaunchBaseMs.Count <= MaxLaunchBaseEntries
            && queue.PendingLaunchBaseMs.Count <= MaxLaunchBaseEntries
            && queue.ActivePlaybackRatePerMille.Count <= MaxLaunchBaseEntries
            && queue.PendingPlaybackRatePerMille.Count <= MaxLaunchBaseEntries)
        {
            return;
        }

        // The JF-723 identity guard rides the shared builder: the just-recorded key
        // is never this trim's evictee, whatever slot the dictionary reused for it.
        HashSet<string> queuedItems = BuildTrimMembershipSet(queue, freshlyRecordedKey);
        TrimPositionMap(queue.ActiveLaunchBaseMs, queuedItems, MaxLaunchBaseEntries);
        TrimPositionMap(queue.PendingLaunchBaseMs, queuedItems, MaxLaunchBaseEntries);
        TrimPositionMap(queue.ActivePlaybackRatePerMille, queuedItems, MaxLaunchBaseEntries);
        TrimPositionMap(queue.PendingPlaybackRatePerMille, queuedItems, MaxLaunchBaseEntries);
    }

    /// <summary>
    /// JF-738: projects queue ItemIds into the bounded maps' own key format for
    /// the trims' queued-membership sets. Every bounded map on a device queue is
    /// keyed <c>ToString("N")</c> at its writers (<see cref="RecordLaunchBase"/>,
    /// <see cref="RecordItemPosition"/>, the PlaybackStopped position write)
    /// while production queue ItemIds are dashed <c>Guid.ToString()</c> (every
    /// <see cref="SetQueue"/> caller stores <c>i.Id.ToString()</c>;
    /// <see cref="Enqueue"/> stores dashed too), so a raw copy of
    /// <see cref="DeviceQueue.ItemIds"/> never matched a map key and the
    /// membership half of the trim policy was inert: the trims ran as pure
    /// insertion/slot-order FIFO and a QUEUED item's entry was evictable like
    /// any other. Entries that do not parse as a GUID keep their raw form, so a
    /// non-GUID key still compares equal to itself. Iterator form: both
    /// consumers sit behind their own count gates
    /// (<see cref="TrimLaunchBaseIfNeeded"/>'s four-map OR gate;
    /// <see cref="RecordStoppedPositionAndTrim"/>'s map-count gate), so the
    /// projection runs only when a map is OVER its cap (NOT a transient state
    /// for a fully-navigated queue, whose maps sit pinned at queue length; see
    /// <see cref="BuildTrimMembershipSet"/>'s honest-cost note), and
    /// <see cref="BuildTrimMembershipSet"/> drains it immediately into the
    /// capacity-hinted membership set (one set, shared by the launch-scope
    /// trim's four maps).
    /// </summary>
    /// <param name="itemIds">The stored queue's item ids, any GUID format.</param>
    /// <returns>The ids re-keyed to "N" where parseable, raw otherwise.</returns>
    internal static IEnumerable<string> NormalizeToMapKeyFormat(IEnumerable<string> itemIds)
    {
        foreach (string id in itemIds)
        {
            yield return Guid.TryParse(id, out Guid parsed) ? parsed.ToString("N") : id;
        }
    }

    /// <summary>
    /// JF-738: the ONE membership-set builder for the bounded-map trims: the
    /// stored queue's ids re-keyed to the maps' "N" format (through
    /// <see cref="NormalizeToMapKeyFormat"/>) plus the caller's fresh key (the
    /// JF-723 identity-guard shape: the entry a write just created is never that
    /// write's own trim's evictee; idempotent on an already-"N" key). Every
    /// <see cref="TrimPositionMap"/> WRITE-ADJACENT call site must consume its
    /// set through this builder: a raw <c>queue.ItemIds</c> set never matches an
    /// "N"-keyed map (the inert-membership bug JF-738 fixed), and the fresh-key
    /// guard belongs beside the normalization, not re-derived per caller.
    /// Capacity-hinted (queue size + the fresh key).
    /// HONEST COST (GM-F3): with membership restored, a map whose backing queue
    /// is fully navigated is pinned at QUEUE LENGTH, not at the cap (every
    /// queued item's entry stays), so an over-cap write on a large queue pays an
    /// O(queue-length) GUID normalization here, under
    /// <see cref="_launchScopeLock"/>; bounded by the write frequencies (a
    /// launch record, a qualifying stop), microseconds at playlist scale. The
    /// pin itself is the documented contract ("entries for queued items all
    /// stay"), not a leak.
    /// </summary>
    /// <param name="queue">The device queue whose ItemIds form the membership.</param>
    /// <param name="freshKey">The "N"-normalized key the caller's write just
    /// created; treated as queued by THIS trim only.</param>
    /// <returns>The queued-membership set in the maps' "N" key format.</returns>
    internal static HashSet<string> BuildTrimMembershipSet(DeviceQueue queue, string freshKey)
    {
        HashSet<string> queuedItems = new(queue.ItemIds.Count + 1, StringComparer.OrdinalIgnoreCase);
        foreach (string normalizedId in NormalizeToMapKeyFormat(queue.ItemIds))
        {
            queuedItems.Add(normalizedId);
        }

        queuedItems.Add(freshKey);
        return queuedItems;
    }

    /// <summary>
    /// JF-738 GM-F4: the ItemPositionState cap, beside its sibling
    /// <see cref="MaxLaunchBaseEntries"/> (the cap governing a manager-owned map
    /// lives on the manager that owns the map and the trim). Internal for the
    /// InternalsVisibleTo test seam (the MaxLaunchBaseEntries idiom): the JF-738
    /// position-trim pins seed their cap pressure from this constant so a cap
    /// change cannot silently degrade them to vacuous green.
    /// </summary>
    internal const int MaxItemPositionStateEntries = 200;

    /// <summary>
    /// JF-522/JF-738: the ONE locked write-then-trim path for ItemPositionState
    /// (the PlaybackStopped handler's position persist). The WRITE and the TRIM
    /// are one locked unit (GM-F1): the write is a structural Add for a new key
    /// (a dictionary version bump; same-key updates are not), and the trim's
    /// per-map enumeration inside <see cref="TrimPositionMap"/> throws on a
    /// concurrent structural change, so a sibling stop's unlocked write on the
    /// same device must not be able to interleave (the JF-425/JF-447 class of
    /// dying inside an event handler before the keep-alive ack Amazon
    /// requires). The membership build shares the same lock against
    /// <see cref="Enqueue"/>'s in-place <see cref="DeviceQueue.ItemIds"/>
    /// mutation (the GM-F2 class). The count gate keeps the whole trim body off
    /// the under-cap happy path (one qualifying stop per track); see
    /// <see cref="BuildTrimMembershipSet"/> for the over-cap cost shape.
    /// JF-738 DoD #2, the fresh-entry guard: the key THIS call just wrote is
    /// treated as queued by THIS call's trim only (the JF-723 identity-guard
    /// shape). Membership protects the stopped item just when it is QUEUED; the
    /// single-item play shape has no queue at all (the JF-424.1
    /// store-unconditional rationale), so without the guard the fresh entry is
    /// evictable by its own trim at cap pressure, and resume-after-pause loses
    /// the seed the JF-581 incident made load-bearing. The guard can leave the
    /// map at cap+1 until the next write ages the entry; the trim still trims
    /// aged non-queued entries.
    /// </summary>
    /// <param name="deviceId">The device whose queue is persisted (debounced).</param>
    /// <param name="queue">The device queue whose ItemPositionState is written and bounded.</param>
    /// <param name="itemId">The stopped item (keyed "N").</param>
    /// <param name="positionTicks">The item-absolute position in ticks.</param>
    /// <param name="cap">The maximum entry count (defaults to
    /// <see cref="MaxItemPositionStateEntries"/>).</param>
    internal void RecordStoppedPositionAndTrim(string deviceId, DeviceQueue queue, Guid itemId, long positionTicks, int cap = MaxItemPositionStateEntries)
    {
        string key = itemId.ToString("N");
        lock (_launchScopeLock)
        {
            queue.ItemPositionState[key] = positionTicks;
            if (queue.ItemPositionState.Count > cap)
            {
                TrimPositionMap(queue.ItemPositionState, BuildTrimMembershipSet(queue, key), cap);
            }
        }

        SchedulePersistInternal(deviceId);
    }

    /// <summary>
    /// JF-738 GM-F2: the ONE locked ItemIds membership read. An unlocked LINQ
    /// Contains on the live list (the stop handler's queue-contradiction check)
    /// enumerates via the List enumerator, whose version check throws when
    /// <see cref="Enqueue"/> mutates the list in place under
    /// <see cref="_launchScopeLock"/> (the JF-425/JF-447 die-before-the-ack
    /// class). OrdinalIgnoreCase preserves the previous comparison semantics.
    /// </summary>
    /// <param name="queue">The device queue whose membership is read.</param>
    /// <param name="itemId">The item id, any casing.</param>
    /// <returns>True when the item is in the stored queue.</returns>
    internal bool IsItemQueued(DeviceQueue queue, string itemId)
    {
        lock (_launchScopeLock)
        {
            return queue.ItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The ONE per-map eviction policy shared by every bounded position map on a
    /// device queue (JF-522; previously three inline copies across this class and
    /// PlaybackStoppedEventHandler): over the cap, remove the oldest entries whose
    /// item is not queued; entries for queued items all stay. The map's own
    /// count gate keeps the set construction off the happy path.
    /// </summary>
    /// <param name="map">The bounded dictionary.</param>
    /// <param name="queuedItems">The queued item ids, compared case-insensitively.
    /// FORMAT CONTRACT (JF-738): an entry is protected only when its KEY is
    /// string-equal (ignoring case) to a member of this set, and every bounded
    /// map is "N"-keyed, so a write-adjacent call site must build its set through
    /// <see cref="BuildTrimMembershipSet"/>; a raw copy of queue ItemIds
    /// (dashed in production) never matches an "N"-keyed map, which is exactly
    /// the inert-membership bug JF-738 fixed. New call sites must not assume
    /// "any key format" matches.</param>
    /// <param name="cap">The maximum entry count.</param>
    /// <typeparam name="T">The map's value type (position ticks, launch bases, per-mille rates).</typeparam>
    internal static void TrimPositionMap<T>(Dictionary<string, T> map, IEnumerable<string> queuedItems, int cap)
    {
        if (map.Count <= cap)
        {
            return;
        }

        HashSet<string> queued = queuedItems as HashSet<string> ?? new HashSet<string>(queuedItems, StringComparer.OrdinalIgnoreCase);
        List<string> keysToRemove = new();
        foreach (var kvp in map)
        {
            if (!queued.Contains(kvp.Key))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        // Remove oldest non-queued entries until under cap
        int toRemove = map.Count - cap;
        foreach (string key in keysToRemove.Take(toRemove))
        {
            map.Remove(key);
        }
    }

    /// <summary>
    /// Carries the reset-surviving per-item stores (positions and the launch-scope
    /// base/rate map family) from an old queue into its replacement (JF-522: one
    /// definition for the surviving-store set, so a fourth surviving store is
    /// wired once, not per reset path). The base and rate maps must be carried
    /// together (the pairing invariant <see cref="WritePendingLaunchScope"/> owns:
    /// a rate entry exists iff its base entry exists).
    /// JF-693 (code-review finding 1): the LAST-PLAYED record survives too. It is a
    /// fact about a LAUNCH, not about the queue's contents, and the PlayBook paths
    /// now call <see cref="SetQueue"/> AFTER the launch builder (the JF-687
    /// refusal-before-phantom-state ordering), so a reset that dropped it would wipe
    /// the record the builder just wrote on every successful book launch (the medium
    /// classifiers read it: SleepTimer/SetPlaybackSpeed JF-632 gates, resume
    /// arbitration). Old-order callers (SetQueue before the launch) are unaffected:
    /// their builder record still lands last and overwrites whatever survived.
    /// </summary>
    /// <param name="oldQueue">The queue being replaced (null starts everything empty).</param>
    /// <param name="queue">The fresh queue to populate.</param>
    private static void CopySurvivingStores(DeviceQueue? oldQueue, DeviceQueue queue)
    {
        queue.ItemPositionState = oldQueue?.ItemPositionState ?? new Dictionary<string, long>();
        queue.ActiveLaunchBaseMs = oldQueue?.ActiveLaunchBaseMs ?? new Dictionary<string, long>();
        queue.PendingLaunchBaseMs = oldQueue?.PendingLaunchBaseMs ?? new Dictionary<string, long>();
        queue.ActivePlaybackRatePerMille = oldQueue?.ActivePlaybackRatePerMille ?? new Dictionary<string, int>();
        queue.PendingPlaybackRatePerMille = oldQueue?.PendingPlaybackRatePerMille ?? new Dictionary<string, int>();
        queue.LastPlayedItemId = oldQueue?.LastPlayedItemId;
        queue.LastPlayedLaunchRoute = oldQueue?.LastPlayedLaunchRoute;

        // JF-693 review (coordinator F1): the FRESHNESS STAMP rides with the record.
        // A carried record without its stamp reads as a legacy entry in
        // GetDeviceResumePointer's tie rule (either stamp null => queue pointer wins),
        // so the just-launched book would lose resume arbitration to the old song's
        // stop-event pointer on the displaced-stop shape.
        queue.LastPlayedWrittenAt = oldQueue?.LastPlayedWrittenAt;

    }

    /// <summary>
    /// Sets the queue for a device and schedules a debounced persist to disk.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemIds">The list of media item IDs for the queue.</param>
    /// <param name="currentIndex">The index of the currently playing item.</param>
    /// <param name="repeatMode">Repeat mode: "None", "One", "All".</param>
    /// <param name="playbackOrder">Playback order: "Default" or "Shuffle".</param>
    public void SetQueue(string deviceId, List<string> itemIds, int currentIndex, string repeatMode = "None", string playbackOrder = "Default")
    {
        // JF-713 gate-marker: the stored queue owns its lists (the ownership
        // contract CommitShuffledQueue already documents); storing the caller's
        // List by reference let a later caller-side mutation silently mutate the
        // live queue, and it blinded the shuffle refusal pin (a seeded sentinel
        // shared by reference could not distinguish untouched from
        // mutated-in-place).
        var queue = new DeviceQueue
        {
            ItemIds = new List<string>(itemIds),
            CurrentIndex = currentIndex,
            RepeatMode = repeatMode,
            PlaybackOrder = playbackOrder,
            LastModifiedUtc = DateTime.UtcNow,
        };
        ReplaceQueue(deviceId, queue);

        _logger.LogDebug(
            "Queue set for device {DeviceId}: {Count} items, index={Index}, repeat={Repeat}, order={Order}",
            deviceId, itemIds.Count, currentIndex, repeatMode, playbackOrder);
    }

    /// <summary>
    /// The ONE queue-replacement tail shared by <see cref="SetQueue"/> and
    /// <see cref="CommitShuffledQueue"/> (the JF-713 code-review finding: the
    /// CopySurvivingStores doc promises a reset-surviving store is wired once, so
    /// the reset tail around it must exist once too; a future tail step added to
    /// one reset path only would silently diverge the other). Carries the
    /// surviving stores off the outgoing queue, installs the replacement, and
    /// schedules the debounced persist.
    /// </summary>
    /// <param name="deviceId">The device whose queue is replaced.</param>
    /// <param name="queue">The replacement queue, fully populated.</param>
    private void ReplaceQueue(string deviceId, DeviceQueue queue)
    {
        CopySurvivingStores(_queues.TryGetValue(deviceId, out DeviceQueue? oldQueue) ? oldQueue : null, queue);

        _queues[deviceId] = queue;
        SchedulePersistInternal(deviceId);
    }

    /// <summary>
    /// DERIVES (but does not store) a freshly-shuffled queue order: snapshots the
    /// original order and Fisher-Yates shuffles ALL items (including position 0, so
    /// the first-played track is random). The playlist shuffle arm's JF-713 derive
    /// step; pair with <see cref="CommitShuffledQueue"/> after the launch build.
    /// <paramref name="rng"/> is injectable for deterministic unit tests; defaults
    /// to the process-global Random.Shared. ADDITIVE: does not alter
    /// SetQueue/ShuffleRemaining/RestoreOrder (JF-301 path).
    /// </summary>
    /// <param name="itemIds">The list of media item IDs to shuffle.</param>
    /// <param name="rng">Optional injectable random source (defaults to Random.Shared).</param>
    /// <returns>The derived snapshot (shuffle order + original order); nothing is written.</returns>
    public PendingShuffledQueue DeriveShuffledQueue(List<string> itemIds, Random? rng = null)
    {
        Random random = rng ?? Random.Shared;

        List<string> original = new List<string>(itemIds);
        List<string> shuffled = new List<string>(itemIds);
        FisherYates(shuffled, random);

        return new PendingShuffledQueue(shuffled, original);
    }

    /// <summary>
    /// Commits a derived shuffle snapshot as the device's queue: stores the
    /// snapshot's shuffled order VERBATIM (no re-shuffle; defensive copies, so the
    /// stored queue owns its lists and a post-commit mutation of the snapshot, or
    /// committing one snapshot to two devices, can never alias a live queue) with
    /// PlaybackOrder=Shuffle + CurrentIndex=0 and the pre-shuffle order for
    /// <see cref="RestoreOrder"/>, carrying surviving stores. The JF-713 commit
    /// step; the caller MUST pass the snapshot its launch build derived from.
    /// Returns the stored queue so the caller can mirror it into the session queue.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="pending">The snapshot derived by <see cref="DeriveShuffledQueue"/>.</param>
    /// <returns>The stored queue (the same instance GetOrCreateQueue returns).</returns>
    public DeviceQueue CommitShuffledQueue(string deviceId, PendingShuffledQueue pending)
    {
        var queue = new DeviceQueue
        {
            ItemIds = new List<string>(pending.ShuffledItemIds),
            OriginalItemIds = new List<string>(pending.OriginalItemIds),
            CurrentIndex = 0,
            RepeatMode = "None",
            PlaybackOrder = "Shuffle",
            LastModifiedUtc = DateTime.UtcNow,
        };
        ReplaceQueue(deviceId, queue);

        _logger.LogDebug(
            "Shuffled queue set for device {DeviceId}: {Count} items, order=Shuffle",
            deviceId, pending.ShuffledItemIds.Count);
        return queue;
    }

    /// <summary>
    /// Where <see cref="Enqueue"/> inserts an item into the queue (JF-578): the
    /// two queue-editing intent shapes.
    /// </summary>
    public enum QueueInsertPlacement
    {
        /// <summary>After the last item (the AddToQueue ask).</summary>
        End,

        /// <summary>
        /// Right after the current item, or at the front when there is no current
        /// item or it is not queued (the PlayNext ask).
        /// </summary>
        AfterCurrent
    }

    /// <summary>
    /// JF-578: the ONE queue-MEMBERSHIP writer for the non-play paths (AddToQueue,
    /// PlayNext, through <c>ProgressReporter.EnqueueToBothStores</c>): inserts an
    /// item into the device queue at the given placement and schedules the debounced
    /// persist, so an add survives a restart instead of living only in the wiped
    /// session queue. Placement is computed against the DEVICE queue (the
    /// authoritative membership store, JF-447): End appends after the last item;
    /// AfterCurrent inserts after <paramref name="currentItemId"/>'s position (the
    /// caller's resolved current item: the session's now-playing item, or on the
    /// JF-577 rehydrated shape the coherent playing token) and falls back to the
    /// FRONT when there is no current item or it is not queued (the PlayNext
    /// session-side fallback shape).
    /// POINTER BOOKKEEPING: <see cref="DeviceQueue.CurrentIndex"/> keeps pointing
    /// at the same physical item (it advances by one exactly when the insertion
    /// point is at or before it); the playback-position pointers
    /// (<see cref="DeviceQueue.CurrentItemId"/> and
    /// <see cref="DeviceQueue.CurrentPositionTicks"/>) are owned by the playback
    /// event writers and are deliberately untouched.
    /// SHUFFLE COHERENCE: on a shuffled queue the insert lands in the physical
    /// (shuffled) <see cref="DeviceQueue.ItemIds"/> order, which is what plays;
    /// when a pre-shuffle snapshot exists the item is also appended to
    /// <see cref="DeviceQueue.OriginalItemIds"/> so <see cref="RestoreOrder"/>
    /// keeps the user's added item instead of silently dropping it (its
    /// restored-order position is the end: the original-order position of a "next"
    /// ask under shuffle is undefined, its membership is not). A missing or empty
    /// queue is SEEDED with the single item and CurrentIndex=-1 (the store records
    /// no current item; that pointer is owned by the play paths'
    /// <see cref="SetQueue"/> and <see cref="MoveTo"/>).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The media item ID to insert.</param>
    /// <param name="placement">Where to insert the item.</param>
    /// <param name="currentItemId">The caller's resolved current item, positioning
    /// the AfterCurrent insert (ignored for End).</param>
    public void Enqueue(string deviceId, Guid itemId, QueueInsertPlacement placement, Guid? currentItemId = null)
    {
        // JF-578 review: the mutation body runs under the launch-scope lock - this is
        // the FIRST in-place ItemIds mutation an intent thread performs, and the
        // event thread concurrently indexes the same list from
        // PlaybackNearlyFinished/MoveTo (the JF-522 two-thread-writer rationale).
        lock (_launchScopeLock)
        {
            DeviceQueue queue = GetOrCreateQueue(deviceId);
            bool seeded = queue.ItemIds.Count == 0;
            int insertIndex = ResolveInsertIndex(queue, placement, currentItemId);

            queue.ItemIds.Insert(insertIndex, itemId.ToString());
            if (seeded)
            {
                // A seeded queue has no current item: the doc contract says the
                // pointer starts at -1, not at the freshly inserted item (review S1;
                // the ternary this replaces was a no-op - ResolveInsertIndex already
                // returns 0 on an empty queue - and an existing queue with ItemIds
                // empty but CurrentIndex >= 0 (SetQueue with an empty list) would
                // otherwise end up pointing past the single seeded item).
                queue.CurrentIndex = -1;
            }
            else if (queue.CurrentIndex >= insertIndex)
            {
                // An insert at or before the pointer shifted that item one position
                // right; advance the pointer so it keeps naming the same item.
                queue.CurrentIndex++;
            }

            queue.OriginalItemIds?.Add(itemId.ToString());
            queue.LastModifiedUtc = DateTime.UtcNow;
            SchedulePersistInternal(deviceId);

            _logger.LogDebug(
                "Enqueue: device {DeviceId} inserted item {ItemId} at index {InsertIndex} ({Placement}{Seed}); queue={Count} items, currentIndex={CurrentIndex}",
                deviceId, itemId, insertIndex, placement, seeded ? ", seeded" : string.Empty, queue.ItemIds.Count, queue.CurrentIndex);
        }
    }

    /// <summary>
    /// The THREE-WAY placement policy, defined once (review finding R1): the end
    /// for End; behind the current for AfterCurrent when the current is found;
    /// the front otherwise. ProgressReporter's session-leg insert passes its own
    /// store's count and current index so both legs cannot drift.
    /// </summary>
    /// <param name="placement">Where to insert.</param>
    /// <param name="count">The store's current item count.</param>
    /// <param name="currentIndex">The store's index of the current item, or -1.</param>
    /// <returns>The insert index.</returns>
    internal static int ResolveInsertPosition(QueueInsertPlacement placement, int count, int currentIndex)
    {
        if (placement == QueueInsertPlacement.End)
        {
            return count;
        }

        return currentIndex >= 0 ? currentIndex + 1 : 0;
    }

    /// <summary>
    /// The insert position <see cref="Enqueue"/> uses against a non-empty queue.
    /// Delegates to <see cref="ResolveInsertPosition"/>: the three-way placement
    /// policy (end; behind the current when queued; front otherwise) has ONE
    /// definition because the session-leg mirror in ProgressReporter encodes the
    /// same policy on a different store type.
    /// </summary>
    private static int ResolveInsertIndex(DeviceQueue queue, QueueInsertPlacement placement, Guid? currentItemId)
    {
        int count = queue.ItemIds.Count;
        int current = currentItemId != null
            ? queue.ItemIds.IndexOf(currentItemId.Value.ToString())
            : -1;
        return ResolveInsertPosition(placement, count, current);
    }

    /// <summary>Fisher–Yates shuffle, in place. Used by DeriveShuffledQueue.
    /// (ShuffleRemaining keeps its own inline loop unchanged — spec non-goal.)</summary>
    private static void FisherYates(List<string> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Advances the current index to the next item in the queue.
    /// Handles RepeatOne (stay on same), RepeatAll (wrap around), and sequential modes.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The next item ID, or null if playback should end.</returns>
    public string? Advance(string deviceId)
    {
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            _logger.LogDebug("Advance: no queue found for device {DeviceId}", deviceId);
            return null;
        }

        if (queue.ItemIds.Count == 0 || queue.CurrentIndex < 0)
        {
            _logger.LogDebug("Advance: empty queue or no current index for device {DeviceId}", deviceId);
            return null;
        }

        int fromIndex = queue.CurrentIndex;

        // RepeatOne: stay on same track
        if (string.Equals(queue.RepeatMode, "One", StringComparison.Ordinal))
        {
            _logger.LogDebug("Advance: device {DeviceId} RepeatOne — staying at index {Index}, item={ItemId}", deviceId, fromIndex, queue.ItemIds[fromIndex]);
            return queue.ItemIds[queue.CurrentIndex];
        }

        int nextIndex = queue.CurrentIndex + 1;

        // Wrap around for RepeatAll
        if (nextIndex >= queue.ItemIds.Count)
        {
            if (string.Equals(queue.RepeatMode, "All", StringComparison.Ordinal))
            {
                nextIndex = 0;
            }
            else
            {
                _logger.LogDebug("Advance: device {DeviceId} reached end of queue at index {Index}, no wrap", deviceId, fromIndex);
                return null;
            }
        }

        queue.CurrentIndex = nextIndex;
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Advance: device {DeviceId} moved from index {FromIndex} to {ToIndex}, next item={ItemId}",
            deviceId, fromIndex, nextIndex, queue.ItemIds[nextIndex]);

        return queue.ItemIds[nextIndex];
    }

    /// <summary>
    /// Moves to a specific item in the queue by its ID.
    /// Used when PlaybackNearlyFinished resolves the next track and needs to update the pointer.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID to move to.</param>
    /// <returns>True if the item was found and pointer updated.</returns>
    public bool MoveTo(string deviceId, string itemId)
    {
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            _logger.LogDebug("MoveTo: no queue found for device {DeviceId}", deviceId);
            return false;
        }

        int index = queue.ItemIds.IndexOf(itemId);
        if (index < 0)
        {
            _logger.LogDebug("MoveTo: item {ItemId} not found in queue for device {DeviceId}", itemId, deviceId);
            return false;
        }

        int fromIndex = queue.CurrentIndex;
        queue.CurrentIndex = index;
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "MoveTo: device {DeviceId} moved from index {FromIndex} to {ToIndex}, item={ItemId}",
            deviceId, fromIndex, index, itemId);

        return true;
    }

    /// <summary>
    /// Updates the repeat mode for a device's queue.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="repeatMode">The new repeat mode.</param>
    public void SetRepeatMode(string deviceId, string repeatMode)
    {
        DeviceQueue queue = GetOrCreateQueue(deviceId);
        queue.RepeatMode = repeatMode;
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);
    }

    /// <summary>
    /// Updates the playback order for a device's queue.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="playbackOrder">The new playback order.</param>
    public void SetPlaybackOrder(string deviceId, string playbackOrder)
    {
        DeviceQueue queue = GetOrCreateQueue(deviceId);
        queue.PlaybackOrder = playbackOrder;
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);
    }

    /// <summary>
    /// Shuffles the queue items after the currently-playing item, keeping the
    /// current item first. Snapshots the original order into
    /// <see cref="DeviceQueue.OriginalItemIds"/> so <see cref="RestoreOrder"/> can
    /// revert. No-op for queues with fewer than three items or when the current
    /// item is already at (or within one of) the end. Used by
    /// <c>ShuffleOnIntentHandler</c> so sequential queue advancement plays a
    /// shuffled order regardless of the indirect Jellyfin PlayState flag.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="currentItemId">The currently-playing item ID (kept first).</param>
    public void ShuffleRemaining(string deviceId, string currentItemId)
    {
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            _logger.LogDebug("ShuffleRemaining: no queue for device {DeviceId}", deviceId);
            return;
        }

        if (queue.ItemIds.Count < 3)
        {
            return;
        }

        int current = queue.ItemIds.IndexOf(currentItemId);
        if (current < 0 || current >= queue.ItemIds.Count - 2)
        {
            // Current not found, or too close to the end to shuffle meaningfully.
            return;
        }

        // Snapshot original order only on first shuffle — don't clobber an
        // existing snapshot if shuffle is toggled repeatedly mid-playback.
        queue.OriginalItemIds ??= new List<string>(queue.ItemIds);

        List<string> head = queue.ItemIds.Take(current + 1).ToList();
        List<string> tail = queue.ItemIds.Skip(current + 1).ToList();

        // Fisher–Yates shuffle on the tail. Uses the process-global Random.Shared
        // (do not seed it — it is shared across callers).
        for (int i = tail.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (tail[i], tail[j]) = (tail[j], tail[i]);
        }

        queue.ItemIds = head.Concat(tail).ToList();
        queue.PlaybackOrder = "Shuffle";
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "ShuffleRemaining: shuffled tail ({Count} items) for device {DeviceId} after index {Index}",
            tail.Count, deviceId, current);
    }

    /// <summary>
    /// Restores the queue to its pre-shuffle order. No-op if the queue is not
    /// currently shuffled (<see cref="DeviceQueue.OriginalItemIds"/> is null).
    /// Used by <c>ShuffleOffIntentHandler</c>.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void RestoreOrder(string deviceId)
    {
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue) || queue.OriginalItemIds == null)
        {
            return;
        }

        queue.ItemIds = new List<string>(queue.OriginalItemIds);
        queue.OriginalItemIds = null;
        queue.PlaybackOrder = "Default";
        queue.LastModifiedUtc = DateTime.UtcNow;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug("RestoreOrder: restored original order for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Clears the queue for a specific device.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void Clear(string deviceId)
    {
        _queues.TryRemove(deviceId, out _);

        // Disarm BEFORE the file delete (JF-449): Disarm is a barrier, so a
        // persist callback that already started finishes its write here, and
        // the delete below removes what it wrote; a queued straggler finds the
        // entry gone and no-ops. Timer.Dispose alone does not wait for an
        // already-started callback, which is how Clear could previously
        // resurrect the deleted queue file.
        _debounce.Disarm(deviceId);

        // Delete persisted file
        string filePath = GetQueueFilePath(deviceId);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete queue file for device {DeviceId}", deviceId);
        }

        _logger.LogDebug("Queue cleared for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Persists all modified queues to disk immediately.
    /// Call during graceful shutdown to ensure no data loss.
    /// </summary>
    public void PersistAll()
    {
        foreach (var kvp in _queues)
        {
            PersistToDisk(kvp.Key, kvp.Value);
        }
    }

    /// <summary>
    /// Gets the number of active device queues.
    /// </summary>
    public int ActiveQueueCount => _queues.Count;

    /// <summary>
    /// Schedules a debounced persist to disk for a device's queue.
    /// Call after modifying queue state directly (e.g., ItemPositionState updates).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void SchedulePersist(string deviceId)
    {
        SchedulePersistInternal(deviceId);
    }

    /// <summary>
    /// Returns all active device queues, optionally excluding a specific device.
    /// Each entry includes the device ID and its queue state.
    /// </summary>
    /// <param name="excludeDeviceId">Optional device ID to exclude from results.</param>
    /// <returns>A list of device-queue pairs for all active devices (excluding the specified one).</returns>
    public List<(string DeviceId, DeviceQueue Queue)> GetAllActiveQueues(string? excludeDeviceId = null)
    {
        var result = new List<(string DeviceId, DeviceQueue Queue)>();

        foreach (var kvp in _queues)
        {
            if (excludeDeviceId != null && string.Equals(kvp.Key, excludeDeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (kvp.Value.ItemIds.Count > 0 && kvp.Value.CurrentIndex >= 0)
            {
                result.Add((kvp.Key, kvp.Value));
            }
        }

        return result;
    }

    private void SchedulePersistInternal(string deviceId)
    {
        // Payload is captured at ARM time, not looked up at fire time (JF-449):
        // every mutation path re-arms, so the armed payload is always the latest
        // state, while a straggler callback can no longer depend on live
        // dictionary state that a Clear may already have removed. The entry
        // check in the debounce gate is what stops such a stale payload.
        if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            return;
        }

        // Arm owns the disposed guard (volatile flag plus in-lock re-check, the
        // JF-429 idiom moved into the shared helper).
        _debounce.Arm(deviceId, () => PersistToDisk(deviceId, queue));
    }

    private void PersistToDisk(string deviceId, DeviceQueue queue)
    {
        string filePath = GetQueueFilePath(deviceId);
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(queue, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist queue for device {DeviceId}", deviceId);
        }
    }

    private void LoadAllFromDisk()
    {
        if (!Directory.Exists(_dataDirectory))
        {
            _logger.LogDebug("Queue data directory does not exist yet: {Path}", _dataDirectory);
            return;
        }

        try
        {
            foreach (string file in Directory.GetFiles(_dataDirectory, "queue_*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    DeviceQueue? queue = JsonSerializer.Deserialize<DeviceQueue>(json, JsonOptions);
                    if (queue != null)
                    {
                        // Extract device ID from filename: queue_<deviceId>.json
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        string deviceId = fileName["queue_".Length..];
                        _queues[deviceId] = queue;
                        _logger.LogDebug("Loaded queue for device {DeviceId}: {Count} items", deviceId, queue.ItemIds.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load queue file: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan queue data directory: {Path}", _dataDirectory);
        }

        _logger.LogInformation("Loaded {Count} device queues from disk", _queues.Count);
    }

    private string GetQueueFilePath(string deviceId)
    {
        // Use a safe filename derived from device ID (which may contain special chars)
        string safeName = deviceId.Replace("/", "_", StringComparison.Ordinal)
                                  .Replace("\\", "_", StringComparison.Ordinal)
                                  .Replace(":", "_", StringComparison.Ordinal);
        return Path.Combine(_dataDirectory, $"queue_{safeName}.json");
    }

    /// <summary>
    /// Dispose debounce timers and persist all queues. Unified teardown order
    /// (JF-449): flag, then timer teardown, then the final flush. The teardown
    /// is a barrier for in-flight persist callbacks, so no debounce write can
    /// run concurrently with (or after) the final flush.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _debounce.Dispose();

        PersistAll();
    }

    /// <summary>
    /// The shared debounce map. Internal test seam: race tests use it to
    /// shrink the interval and to park a callback mid-flight
    /// (<see cref="KeyedOneShotDebounce.BeforeCallbackGate"/>).
    /// </summary>
    internal KeyedOneShotDebounce TestDebounce => _debounce;

    /// <summary>
    /// Fire the device's pending debounce payload synchronously (test seam for
    /// the JF-449 interleavings; no-op when disarmed or disposed).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    internal void FirePersistForTest(string deviceId) => _debounce.FireNow(deviceId);
}
