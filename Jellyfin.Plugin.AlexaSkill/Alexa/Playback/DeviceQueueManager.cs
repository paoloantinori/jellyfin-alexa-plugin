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
    /// Records the last user-initiated play on a device. Called from the
    /// BuildAudioPlayerResponse chokepoint on every ReplaceAll play, so it captures
    /// all play paths (intent handlers, APL carousel taps, resume confirmations).
    /// This is the device-specific source of truth that survives VideoApp.Launch
    /// plays which do not update context.AudioPlayer.Token.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemId">The item ID that was played.</param>
    public void RecordLastPlayed(string deviceId, string itemId)
    {
        DeviceQueue queue = GetOrCreateQueue(deviceId);

        // Short-circuit when the item hasn't changed — avoids timer churn and
        // redundant disk writes when the same item is replayed or re-issued.
        if (string.Equals(queue.LastPlayedItemId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        queue.LastPlayedItemId = itemId;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Recorded last played for device {DeviceId}: item={ItemId}",
            deviceId, itemId);
    }

    /// <summary>
    /// Gets the last-played item ID for a device without creating a queue entry.
    /// Read-side counterpart to <see cref="RecordLastPlayed"/>. Returns null if the
    /// device has no recorded play.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The last-played item ID, or null if none recorded.</returns>
    public string? GetLastPlayedItemId(string deviceId)
    {
        return _queues.TryGetValue(deviceId, out DeviceQueue? queue)
            ? queue.LastPlayedItemId
            : null;
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
