using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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

    /// <summary>The single debounce key (one shared persist file, one timer).</summary>
    internal const string PersistDebounceKey = "persist";

    // bookParentId (GUID "N"-format string) → highest segment number seen
    private readonly ConcurrentDictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly KeyedOneShotDebounce _debounce = new(PersistDebounce);
    private readonly string _dataFilePath;
    private readonly ILogger<AudiobookPositionTracker> _logger;
    private volatile bool _disposed;

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
    /// <param name="bookParentId">The audiobook parent-folder ID (any GUID format — normalized internally).</param>
    /// <param name="segmentNumber">The zero-based segment index fetched.</param>
    public void RecordSegment(string bookParentId, int segmentNumber)
    {
        if (string.IsNullOrEmpty(bookParentId) || segmentNumber < 0)
        {
            return;
        }

        string key = NormalizeKey(bookParentId);
        _positions.TryGetValue(key, out int previous);
        if (segmentNumber <= previous)
        {
            return; // ignore backward/seek fetches below the high-water mark
        }

        _positions[key] = segmentNumber;
        SchedulePersist();
        _logger.LogDebug(
            "AudiobookPositionTracker: book {BookId} advanced to segment {Segment} (from {Previous})",
            key, segmentNumber, previous);
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

        string key = NormalizeKey(bookParentId);
        if (!_positions.TryGetValue(key, out int highWaterMark) || highWaterMark <= 0)
        {
            return 0;
        }

        int conservativeSegment = Math.Max(0, highWaterMark - 1);
        return conservativeSegment * SegmentDurationSeconds * TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// Clear tracked position for a book (e.g. when the book is finished/marked played).
    /// </summary>
    /// <param name="bookParentId">The audiobook parent-folder ID (any GUID format).</param>
    public void Clear(string bookParentId)
    {
        if (_positions.TryRemove(NormalizeKey(bookParentId), out _))
        {
            SchedulePersist();
        }
    }

    /// <summary>
    /// Normalize a book ID to a canonical key (GUID "N" format, no dashes) so the record
    /// path (raw URL itemId with dashes) and the read path (ToString("N")) match. Falls back
    /// to the raw input if it isn't a GUID.
    /// </summary>
    private static string NormalizeKey(string bookParentId)
    {
        return Guid.TryParse(bookParentId, out Guid g) ? g.ToString("N") : bookParentId;
    }

    private void SchedulePersist()
    {
        // Arm owns the disposed guard (volatile flag plus in-lock re-check, the
        // JF-429 idiom moved into the shared KeyedOneShotDebounce helper).
        _debounce.Arm(PersistDebounceKey, PersistToDisk);
    }

    private void PersistToDisk()
    {
        string tempPath = _dataFilePath + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(_dataFilePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(_positions, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _dataFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audiobook positions to {Path}", _dataFilePath);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private void LoadFromDisk()
    {
        // Best-effort cleanup of a stale .tmp from a prior interrupted write
        try { File.Delete(_dataFilePath + ".tmp"); } catch { }

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
    /// <remarks>Unified teardown order (JF-449): flag, timer teardown, final
    /// flush. The teardown is a barrier for an in-flight debounce callback, so
    /// the final flush is the last writer of the shared .tmp path: no
    /// concurrent write failure can be swallowed by PersistToDisk's catch,
    /// leaving the previous on-disk content in place.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _debounce.Dispose();

        PersistToDisk(); // final flush
    }

    /// <summary>
    /// The shared debounce map. Internal test seam: race tests use it to
    /// shrink the interval and to park a callback mid-flight
    /// (<see cref="KeyedOneShotDebounce.BeforeCallbackGate"/>).
    /// </summary>
    internal KeyedOneShotDebounce TestDebounce => _debounce;

    /// <summary>
    /// Fire the pending debounce payload synchronously (test seam for the
    /// JF-449 interleavings; no-op when disarmed or disposed).
    /// </summary>
    internal void FirePersistForTest() => _debounce.FireNow(PersistDebounceKey);
}
