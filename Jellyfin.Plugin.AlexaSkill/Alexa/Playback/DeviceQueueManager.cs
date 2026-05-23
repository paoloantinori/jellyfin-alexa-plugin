using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.Ordinal);
    private readonly string _dataDirectory;
    private readonly ILogger<DeviceQueueManager> _logger;
    private bool _disposed;

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
    /// Sets the queue for a device and schedules a debounced persist to disk.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="itemIds">The list of media item IDs for the queue.</param>
    /// <param name="currentIndex">The index of the currently playing item.</param>
    /// <param name="repeatMode">Repeat mode: "None", "One", "All".</param>
    /// <param name="playbackOrder">Playback order: "Default" or "Shuffle".</param>
    public void SetQueue(string deviceId, List<string> itemIds, int currentIndex, string repeatMode = "None", string playbackOrder = "Default")
    {
        // Preserve ItemPositionState across queue resets (survives audiobook switches)
        Dictionary<string, long>? existingPositions = null;
        if (_queues.TryGetValue(deviceId, out DeviceQueue? oldQueue))
        {
            existingPositions = oldQueue.ItemPositionState;
        }

        var queue = new DeviceQueue
        {
            ItemIds = itemIds,
            CurrentIndex = currentIndex,
            RepeatMode = repeatMode,
            PlaybackOrder = playbackOrder,
            LastModifiedUtc = DateTime.UtcNow,
            ItemPositionState = existingPositions ?? new Dictionary<string, long>()
        };

        _queues[deviceId] = queue;
        SchedulePersistInternal(deviceId);

        _logger.LogDebug(
            "Queue set for device {DeviceId}: {Count} items, index={Index}, repeat={Repeat}, order={Order}",
            deviceId, itemIds.Count, currentIndex, repeatMode, playbackOrder);
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
    /// Clears the queue for a specific device.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public void Clear(string deviceId)
    {
        _queues.TryRemove(deviceId, out _);

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

        // Dispose and remove debounce timer
        if (_debounceTimers.TryRemove(deviceId, out Timer? timer))
        {
            timer.Dispose();
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
        _debounceTimers.AddOrUpdate(
            deviceId,
            _ => new Timer(_ => PersistDevice(deviceId), null, DebounceInterval, Timeout.InfiniteTimeSpan),
            (_, existingTimer) =>
            {
                existingTimer.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
                return existingTimer;
            });
    }

    private void PersistDevice(string deviceId)
    {
        if (_queues.TryGetValue(deviceId, out DeviceQueue? queue))
        {
            PersistToDisk(deviceId, queue);
        }
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
    /// Dispose debounce timers and persist all queues.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        PersistAll();

        foreach (var kvp in _debounceTimers)
        {
            kvp.Value.Dispose();
        }

        _debounceTimers.Clear();
    }
}
