using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Global playback-position tracker for audiobooks played via the HLS concat endpoint.
/// Keyed by book parent-folder ID because segment requests are anonymous (no device/user/
/// api_key). Single-user skill — global keying is acceptable.
///
/// Tracks the high-water-mark segment number seen via GetSegment requests and reports a
/// conservative resume position: (highWaterMark - 1) * segmentDuration, so resume never
/// skips ahead of what the player has actually fetched (the last-fetched segment may be
/// buffered/prefetched but not yet played).
/// </summary>
public sealed class AudiobookPositionTracker : IDisposable
{
    private const int SegmentDurationSeconds = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan PersistDebounce = TimeSpan.FromSeconds(3);

    // bookParentId (GUID "N"-format string) → highest segment number seen
    private readonly ConcurrentDictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Timer> _persistTimers = new(StringComparer.Ordinal);
    private readonly string _dataFilePath;
    private readonly ILogger<AudiobookPositionTracker> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookPositionTracker"/> class.
    /// Loads persisted positions from disk.
    /// </summary>
    /// <param name="dataDirectory">Directory holding the persistence file.</param>
    /// <param name="logger">Logger instance.</param>
    public AudiobookPositionTracker(string dataDirectory, ILogger<AudiobookPositionTracker> logger)
    {
        _dataFilePath = Path.Combine(dataDirectory, "audiobook-positions.json");
        _logger = logger;
        LoadFromDisk();
    }

    /// <summary>
    /// Record that a segment was requested for a book. Updates the high-water mark if this
    /// segment is further than the current one. Debounced per-book persistence.
    /// </summary>
    /// <param name="bookParentId">The audiobook parent-folder ID (GUID "N" format).</param>
    /// <param name="segmentNumber">The zero-based segment index fetched.</param>
    public void RecordSegment(string bookParentId, int segmentNumber)
    {
        if (string.IsNullOrEmpty(bookParentId) || segmentNumber < 0)
        {
            return;
        }

        _positions.AddOrUpdate(bookParentId, segmentNumber, (_, existing) => Math.Max(existing, segmentNumber));
        SchedulePersist();
    }

    /// <summary>
    /// Get the conservative resume position in ticks for a book.
    /// Returns (highWaterMark - 1) * 10s if a high-water mark ≥ 1 is recorded, else 0.
    /// The -1 ensures resume targets the start of the last fully-fetched segment rather
    /// than a segment that may only have been prefetched.
    /// </summary>
    /// <param name="bookParentId">The audiobook parent-folder ID (GUID "N" format).</param>
    /// <returns>Resume position in ticks (conservative), or 0 if none recorded.</returns>
    public long GetPositionTicks(string bookParentId)
    {
        if (string.IsNullOrEmpty(bookParentId))
        {
            return 0;
        }

        if (!_positions.TryGetValue(bookParentId, out int highWaterMark) || highWaterMark <= 0)
        {
            return 0;
        }

        int conservativeSegment = Math.Max(0, highWaterMark - 1);
        return conservativeSegment * SegmentDurationSeconds * TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// Clear tracked position for a book (e.g. when the book is finished/marked played).
    /// </summary>
    /// <param name="bookParentId">The audiobook parent-folder ID (GUID "N" format).</param>
    public void Clear(string bookParentId)
    {
        if (_positions.TryRemove(bookParentId, out _))
        {
            SchedulePersist();
        }
    }

    private void SchedulePersist()
    {
        // Debounce: reset a single shared timer on each write; persist once after 3s of quiet.
        _persistTimers.AddOrUpdate(
            "persist",
            _ => new Timer(_ => PersistToDisk(), null, PersistDebounce, Timeout.InfiniteTimeSpan),
            (_, existing) =>
            {
                existing.Change(PersistDebounce, Timeout.InfiniteTimeSpan);
                return existing;
            });
    }

    private void PersistToDisk()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_dataFilePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(_positions, JsonOptions);
            File.WriteAllText(_dataFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audiobook positions to {Path}", _dataFilePath);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataFilePath))
            {
                return;
            }

            string json = File.ReadAllText(_dataFilePath);
            var loaded = JsonSerializer.Deserialize<ConcurrentDictionary<string, int>>(json, JsonOptions);
            if (loaded != null)
            {
                foreach (var kvp in loaded)
                {
                    _positions[kvp.Key] = kvp.Value;
                }

                _logger.LogInformation("Loaded {Count} audiobook positions from disk", _positions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audiobook positions from {Path}", _dataFilePath);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var kvp in _persistTimers)
        {
            kvp.Value.Dispose();
        }

        _persistTimers.Clear();
        PersistToDisk(); // final flush
    }
}
