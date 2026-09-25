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
    public void RecordLaunchBase(string deviceId, string itemId, long baseMs, bool enqueued)
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
                if (queue.PendingLaunchBaseMs.TryGetValue(key, out long existingPending) && existingPending == baseMs)
                {
                    return;
                }

                queue.PendingLaunchBaseMs[key] = baseMs;
            }
            else
            {
                if (queue.ActiveLaunchBaseMs.TryGetValue(key, out long existingActive) && existingActive == baseMs
                    && !queue.PendingLaunchBaseMs.ContainsKey(key))
                {
                    return;
                }

                queue.ActiveLaunchBaseMs[key] = baseMs;
                queue.PendingLaunchBaseMs.Remove(key);
            }

            TrimLaunchBaseIfNeeded(queue);
        }

        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Recorded {Scope} launch base for device {DeviceId}: item={ItemId}, base={BaseMs}ms",
            enqueued ? "pending" : "active", deviceId, itemId, baseMs);
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
        lock (_launchScopeLock)
        {
            if (!queue.PendingLaunchBaseMs.Remove(key, out baseMs))
            {
                return;
            }

            queue.ActiveLaunchBaseMs[key] = baseMs;
        }

        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Promoted pending launch base to active for device {DeviceId}: item={ItemId}, base={BaseMs}ms",
            deviceId, itemId, baseMs);
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
    /// Bounds both launch-scope dictionaries exactly like the sibling trims
    /// (JF-514/JF-522): over the cap, remove the oldest entries whose item is not in
    /// the current queue; entries for queued items all stay.
    /// </summary>
    private const int MaxLaunchBaseEntries = 200;

    private static void TrimLaunchBaseIfNeeded(DeviceQueue queue)
    {
        if (queue.ActiveLaunchBaseMs.Count <= MaxLaunchBaseEntries
            && queue.PendingLaunchBaseMs.Count <= MaxLaunchBaseEntries)
        {
            return;
        }

        HashSet<string> queuedItems = new(queue.ItemIds, StringComparer.OrdinalIgnoreCase);
        TrimPositionMap(queue.ActiveLaunchBaseMs, queuedItems, MaxLaunchBaseEntries);
        TrimPositionMap(queue.PendingLaunchBaseMs, queuedItems, MaxLaunchBaseEntries);
    }

    /// <summary>
    /// The ONE per-map eviction policy shared by every bounded position map on a
    /// device queue (JF-522; previously three inline copies across this class and
    /// PlaybackStoppedEventHandler): over the cap, remove the oldest entries whose
    /// item is not queued; entries for queued items all stay. The map's own
    /// count gate keeps the set construction off the happy path.
    /// </summary>
    /// <param name="map">The bounded dictionary.</param>
    /// <param name="queuedItems">The queued item ids (any key format; compared case-insensitively).</param>
    /// <param name="cap">The maximum entry count.</param>
    internal static void TrimPositionMap(Dictionary<string, long> map, IEnumerable<string> queuedItems, int cap)
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
    /// Carries the reset-surviving per-item stores (positions and both launch-scope
    /// maps) from an old queue into its replacement (JF-522: one definition for the
    /// surviving-store set, so a fourth surviving store is wired once, not per reset
    /// path).
    /// </summary>
    /// <param name="oldQueue">The queue being replaced (null starts everything empty).</param>
    /// <param name="queue">The fresh queue to populate.</param>
    private static void CopySurvivingStores(DeviceQueue? oldQueue, DeviceQueue queue)
    {
        queue.ItemPositionState = oldQueue?.ItemPositionState ?? new Dictionary<string, long>();
        queue.ActiveLaunchBaseMs = oldQueue?.ActiveLaunchBaseMs ?? new Dictionary<string, long>();
        queue.PendingLaunchBaseMs = oldQueue?.PendingLaunchBaseMs ?? new Dictionary<string, long>();
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
        var queue = new DeviceQueue
        {
            ItemIds = itemIds,
            CurrentIndex = currentIndex,
            RepeatMode = repeatMode,
            PlaybackOrder = playbackOrder,
            LastModifiedUtc = DateTime.UtcNow,
        };
        CopySurvivingStores(_queues.TryGetValue(deviceId, out DeviceQueue? oldQueue) ? oldQueue : null, queue);

        _queues[deviceId] = queue;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Queue set for device {DeviceId}: {Count} items, index={Index}, repeat={Repeat}, order={Order}",
            deviceId, itemIds.Count, currentIndex, repeatMode, playbackOrder);
    }

    /// <summary>
    /// Sets a freshly-shuffled queue for a device. Snapshots the original order into
    /// <see cref="DeviceQueue.OriginalItemIds"/>, Fisher–Yates shuffles ALL items
    /// (including position 0, so the first-played track is random), and sets
    /// PlaybackOrder=Shuffle + CurrentIndex=0. Used by <c>ShufflePlayIntentHandler</c>
    /// to start a playlist already shuffled. <paramref name="rng"/> is injectable for
    /// deterministic unit tests; defaults to the process-global Random.Shared.
    /// ADDITIVE: does not alter SetQueue/ShuffleRemaining/RestoreOrder (JF-301 path).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemIds">The list of media item IDs to shuffle and store.</param>
    /// <param name="rng">Optional injectable random source (defaults to Random.Shared).</param>
    public void SetShuffledQueue(string deviceId, List<string> itemIds, Random? rng = null)
    {
        Random random = rng ?? Random.Shared;

        List<string> original = new List<string>(itemIds);
        List<string> shuffled = new List<string>(itemIds);
        FisherYates(shuffled, random);

        var queue = new DeviceQueue
        {
            ItemIds = shuffled,
            OriginalItemIds = original,
            CurrentIndex = 0,
            RepeatMode = "None",
            PlaybackOrder = "Shuffle",
            LastModifiedUtc = DateTime.UtcNow,
        };
        CopySurvivingStores(_queues.TryGetValue(deviceId, out DeviceQueue? oldQueue) ? oldQueue : null, queue);

        _queues[deviceId] = queue;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Shuffled queue set for device {DeviceId}: {Count} items, order=Shuffle",
            deviceId, shuffled.Count);
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

    /// <summary>Fisher–Yates shuffle, in place. Used by SetShuffledQueue.
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
