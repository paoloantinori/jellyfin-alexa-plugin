using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Global playback-position tracker for audiobooks played via the HLS concat endpoint.
/// Keyed by the audiobook key the resume path reads (the book parent-folder ID)
/// because segment requests are anonymous (no device/user/api_key). Single-user
/// skill, global keying acceptable. The root-level single-file book's own ID is a
/// key shape the book-key resolver can return but that must stay COLD:
/// the record gate (JF-694) deliberately never writes it, because a warm root key
/// arms the dead audiobook/{ownId} resume URL the concat endpoint 404s on (a leaf
/// has no AudioBook children). Do not re-widen the gate.
///
/// Tracks the high-water-mark segment number seen via GetSegment requests and reports a
/// conservative resume position: (highWaterMark - 1) * segmentDuration, so resume never
/// skips ahead of what the player has actually fetched (the last-fetched segment may be
/// buffered/prefetched but not yet played).
///
/// JF-787: each book's marks are SLOTTED BY TIMELINE IDENTITY (the
/// (chapterCount, durationTicks) pair the JF-784 serve verdict uses, read off
/// the encode sidecar by the record gate), so timelines never interact at the
/// write: a foreign-timeline write during the accepted during-encode serve
/// residual grows its OWN slot and can neither destroy the listener's
/// same-timeline mark nor suppress its progress. The serve-side slice resolver
/// (<see cref="ResolveServeSliceTicks"/>) reads the live timeline's own slot,
/// so a foreign-timeline position cannot poison a re-encoded timeline's resume
/// once the encode window closes (it READS AS ABSENT for that timeline).
/// </summary>
public sealed class AudiobookPositionTracker : IDisposable
{
    // LOAD-BEARING COUPLING: must move together with the controller's
    // AudiobookHlsSegmentSeconds and the -hls_time 10 literal in
    // BuildHlsAudiobookFfmpegArguments (the resume arithmetic assumes the segment
    // length ffmpeg actually cut audiobook segments at). RecordScaledSegment divides
    // foreign-timeline indexes by the same const, so it moves with it too.
    private const int SegmentDurationSeconds = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan PersistDebounce = TimeSpan.FromSeconds(3);

    /// <summary>The single debounce key (one shared persist file, one timer).</summary>
    internal const string PersistDebounceKey = "persist";

    // bookParentId (GUID "N"-format string) → the book's per-timeline mark slots
    // (JF-787; one slot per timeline identity the book has been played under, plus
    // at most one identity-less legacy slot). Copy-on-write arrays: a record swaps
    // in a new array, last writer wins a same-book race, the benign loss class the
    // pre-JF-787 dictionary already had.
    private readonly ConcurrentDictionary<string, TimelinePosition[]> _positions = new(StringComparer.Ordinal);
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
    /// Record that a segment was requested for a book. Advances the mark of the
    /// slot whose timeline identity the write carries (creating it on first
    /// advance), never touching any other timeline's slot. Debounced per-book
    /// persistence.
    /// </summary>
    /// <param name="bookParentId">The audiobook book key the resume path reads (any GUID format; normalized internally).</param>
    /// <param name="segmentNumber">The zero-based segment index fetched.</param>
    /// <param name="identity">The timeline the segment indexes (the encode sidecar's pair); null when the serving cache carries no identity.</param>
    public void RecordSegment(string bookParentId, int segmentNumber, TimelineIdentity? identity = null)
    {
        if (string.IsNullOrEmpty(bookParentId) || segmentNumber <= 0)
        {
            return;
        }

        string key = NormalizeKey(bookParentId);
        _positions.TryGetValue(key, out TimelinePosition[]? previous);
        TimelinePosition[] updated = _positions.AddOrUpdate(
            key,
            static (_, incoming) => new[] { incoming },
            static (_, current, incoming) => RecordSlot(current, incoming.HighWaterSegment, incoming.Identity),
            new TimelinePosition(segmentNumber, identity));
        if (ReferenceEquals(updated, previous))
        {
            return; // no mark change (backward/seek fetch below the slot's high-water mark)
        }

        SchedulePersist();
        _logger.LogDebug(
            "AudiobookPositionTracker: book {BookId} advanced to segment {Segment} on timeline {ChapterCount} chapters / {DurationTicks} ticks",
            key, segmentNumber, identity?.ChapterCount, identity?.DurationTicks);
    }

    /// <summary>
    /// The ONE slot-advance rule (JF-787): advance ONLY the write's own timeline's
    /// slot, monotonically (the conservative floor survives seeks and prefetches),
    /// and leave every other timeline's slot alone: marks on different timelines
    /// are incomparable, so no cross-slot max, floor, or reset exists by design
    /// (a reset was the code-review F1 shape: one interleaved foreign fetch
    /// destroyed the listener's legitimate same-timeline mark). A first write of
    /// segment 0 creates no slot: its conservative read is 0 anyway.
    /// </summary>
    /// <param name="slots">The book's current slots.</param>
    /// <param name="segmentNumber">The fetched segment index (greater than 0).</param>
    /// <param name="identity">The timeline the fetch indexes.</param>
    /// <returns>The new slots array, or the SAME array when nothing advanced.</returns>
    private static TimelinePosition[] RecordSlot(TimelinePosition[] slots, int segmentNumber, TimelineIdentity? identity)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Identity == identity)
            {
                if (segmentNumber <= slots[i].HighWaterSegment)
                {
                    return slots;
                }

                var advanced = new TimelinePosition[slots.Length];
                slots.CopyTo(advanced, 0);
                advanced[i] = new TimelinePosition(segmentNumber, identity);
                return advanced;
            }
        }

        var appended = new TimelinePosition[slots.Length + 1];
        slots.CopyTo(appended, 0);
        appended[^1] = new TimelinePosition(segmentNumber, identity);
        return appended;
    }

    /// <summary>
    /// Record a segment fetch for a book whose serve cuts segments at a NON-concat
    /// length: the one-chapter redirect plays the single chapter through the
    /// single-item core (4s segments), while every read of this tracker
    /// (<see cref="GetPositionTicks(string)"/>) counts the 10s concat timeline. Translates the
    /// foreign-timeline index onto this tracker's timeline (floor division, so the
    /// translation is conservative like the high-water-mark -1 itself) before recording.
    /// A raw 4s index recorded as-is would read back 2.5x past the listening point
    /// (segment 150 = 10 minutes listened would resume at 24:50).
    /// </summary>
    /// <param name="bookParentId">The audiobook key the resume path reads (any GUID format; normalized internally).</param>
    /// <param name="segmentNumber">The zero-based segment index fetched, counted in <paramref name="segmentSeconds"/> units.</param>
    /// <param name="segmentSeconds">The segment length the serve actually cut (the caller's own geometry constant).</param>
    /// <param name="identity">The timeline the segment indexes (the single-chapter shape stamps its own runtime); null when unknown.</param>
    public void RecordScaledSegment(string bookParentId, int segmentNumber, int segmentSeconds, TimelineIdentity? identity = null)
    {
        if (segmentSeconds <= 0)
        {
            // Debug-logging policy: a skipped record must be visible in triage, or a
            // bad geometry value silently keeps one-chapter resume cold.
            _logger.LogDebug(
                "AudiobookPositionTracker: ignoring scaled record for book {BookId} with invalid segment seconds {SegmentSeconds}",
                bookParentId, segmentSeconds);
            return;
        }

        RecordSegment(bookParentId, segmentNumber * segmentSeconds / SegmentDurationSeconds, identity);
    }

    /// <summary>
    /// Get the conservative resume position in ticks for a book (identity-agnostic:
    /// the HIGHEST mark across the book's timeline slots, the best-known answer to
    /// "where did the user last reach"). The resume MINT sites use this read; the
    /// timeline the mark belongs to is resolved at the serve
    /// (<see cref="ResolveServeSliceTicks"/>), the only place holding the serving
    /// enumeration.
    /// </summary>
    /// <param name="bookParentId">The audiobook book key (GUID "N" format).</param>
    /// <returns>Resume position in ticks (conservative), or 0 if none recorded.</returns>
    public long GetPositionTicks(string bookParentId)
    {
        if (string.IsNullOrEmpty(bookParentId)
            || !_positions.TryGetValue(NormalizeKey(bookParentId), out TimelinePosition[]? slots))
        {
            return 0;
        }

        int maxMark = 0;
        foreach (TimelinePosition slot in slots)
        {
            if (slot.HighWaterSegment > maxMark)
            {
                maxMark = slot.HighWaterSegment;
            }
        }

        return ConservativeTicks(maxMark);
    }

    /// <summary>
    /// The serve-side slice resolver (JF-787): arbitrate the resume slice the mint
    /// sites produced, on the ONE timeline that actually serves it.
    /// <list type="bullet">
    /// <item>The expected timeline holds a slot: return its mark (the common case
    /// is byte-identical to the minted ticks; a mint that read a HIGHER foreign
    /// slot is re-sliced down to this timeline's own truth).</item>
    /// <item>An identity-less legacy slot holds a mark: return it. The legacy
    /// entry is honored under every timeline (the JF-812 kindless-legacy
    /// precedent): its mark is the best timeline-agnostic truth, safer than both
    /// the minted value (which the raw max may have taken from a foreign slot)
    /// and a drop.</item>
    /// <item>Only OTHER identity-bearing timelines hold marks: the position READS
    /// AS ABSENT for this timeline, return 0 (the caller drops the slice) instead
    /// of mapping a foreign-timeline offset onto re-encoded content.</item>
    /// <item>No usable opinion (no slots, or positionally-empty slots): return
    /// the minted ticks unchanged, today's behavior. A cold tracker must not
    /// drop a legitimately minted non-tracker slice (the Yes-side resume
    /// fallback).</item>
    /// </list>
    /// </summary>
    /// <param name="bookParentId">The audiobook book key (any GUID format; normalized internally).</param>
    /// <param name="expectedChapterCount">The chapter count of the timeline the serve enumerated.</param>
    /// <param name="expectedDurationTicks">The runtime sum of the timeline the serve enumerated.</param>
    /// <param name="mintedTicks">The resume ticks the mint site put in the URL.</param>
    /// <returns>The ticks the serve should slice at (0 drops the slice; the minted ticks pass through on no opinion).</returns>
    public long ResolveServeSliceTicks(string bookParentId, int expectedChapterCount, long expectedDurationTicks, long mintedTicks)
    {
        if (string.IsNullOrEmpty(bookParentId)
            || !_positions.TryGetValue(NormalizeKey(bookParentId), out TimelinePosition[]? slots))
        {
            return mintedTicks;
        }

        int liveMark = 0;
        int legacyMark = 0;
        bool foreignMarkPresent = false;
        foreach (TimelinePosition slot in slots)
        {
            if (slot.Identity is not { } identity)
            {
                legacyMark = Math.Max(legacyMark, slot.HighWaterSegment);
            }
            else if (identity.ChapterCount == expectedChapterCount && identity.DurationTicks == expectedDurationTicks)
            {
                liveMark = Math.Max(liveMark, slot.HighWaterSegment);
            }
            else if (slot.HighWaterSegment > 0)
            {
                foreignMarkPresent = true;
            }
        }

        if (liveMark > 0)
        {
            return ConservativeTicks(liveMark);
        }

        if (legacyMark > 0)
        {
            return ConservativeTicks(legacyMark);
        }

        return foreignMarkPresent ? 0 : mintedTicks;
    }

    private static long ConservativeTicks(int highWaterSegment)
        => Math.Max(0, highWaterSegment - 1) * SegmentDurationSeconds * TimeSpan.TicksPerSecond;

    /// <summary>
    /// Clear tracked position for a book (e.g. when the book is finished/marked played).
    /// </summary>
    /// <param name="bookParentId">The audiobook book key (any GUID format).</param>
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
    /// to the raw input if it isn't a GUID. JF-741: delegates to the ONE shared
    /// parse-or-raw rule (<see cref="DeviceQueueManager.NormalizeToMapKeyFormat(string)"/>).
    /// </summary>
    private static string NormalizeKey(string bookParentId)
    {
        return DeviceQueueManager.NormalizeToMapKeyFormat(bookParentId);
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

            // JF-787: the persisted value changed from a bare int (the
            // high-water mark) to the slot ARRAY (marks + timeline identities).
            // The load walks raw JsonElements so BOTH shapes load: a
            // pre-upgrade file's numbers become IDENTITY-LESS slots (the
            // recorded legacy decision: they keep today's honored read
            // behavior, the JF-812 kindless-legacy precedent), and a torn slot
            // missing or half-carrying its identity degrades to identity-less
            // rather than dropping the position.
            string json = File.ReadAllText(_dataFilePath);
            using JsonDocument doc = JsonDocument.Parse(json);
            int loaded = 0;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (ReadPersistedSlots(property.Value) is { } slots && slots.Length > 0)
                {
                    _positions[property.Name] = slots;
                    loaded++;
                }
            }

            _logger.LogInformation("Loaded {Count} audiobook positions from disk", loaded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audiobook positions from {Path}", _dataFilePath);
        }
    }

    /// <summary>
    /// Read one persisted value in either historical shape (JF-787): a bare JSON
    /// number (pre-upgrade file; one identity-less slot) or the slot ARRAY.
    /// Returns null for values in neither shape; malformed slots are skipped
    /// individually.
    /// </summary>
    /// <param name="value">The raw persisted value.</param>
    /// <returns>The slots, or null when the value is malformed.</returns>
    private static TimelinePosition[]? ReadPersistedSlots(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int legacyMark) && legacyMark > 0)
        {
            return new[] { new TimelinePosition(legacyMark, null) };
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var slots = new List<TimelinePosition>();
            foreach (JsonElement element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("highWaterSegment", out JsonElement mark)
                    && mark.ValueKind == JsonValueKind.Number
                    && mark.TryGetInt32(out int highWaterSegment))
                {
                    slots.Add(new TimelinePosition(highWaterSegment, ReadPersistedIdentity(element)));
                }
            }

            return slots.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Read the nested identity object of a persisted slot; null (identity-less)
    /// when absent or only half-present, so a torn write never loads as a
    /// half-identity the slot and resolver rules would treat differently.
    /// </summary>
    private static TimelineIdentity? ReadPersistedIdentity(JsonElement slot)
    {
        if (slot.TryGetProperty("identity", out JsonElement identity)
            && identity.ValueKind == JsonValueKind.Object
            && identity.TryGetProperty("chapterCount", out JsonElement count)
            && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out int chapterCount)
            && identity.TryGetProperty("durationTicks", out JsonElement ticks)
            && ticks.ValueKind == JsonValueKind.Number
            && ticks.TryGetInt64(out long durationTicks))
        {
            return new TimelineIdentity(chapterCount, durationTicks);
        }

        return null;
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

/// <summary>
/// One tracked position slot: the high-water segment mark plus the TIMELINE IDENTITY
/// (JF-787) the mark-setting write carried. The identity and the mark are ONE
/// atomic fact: whichever write sets the mark supplies the identity, and a null
/// identity means the mark's timeline is unknown (pre-upgrade persisted entries,
/// sidecar-less caches). Identity-less entries keep today's read behavior
/// (honored under any timeline), the JF-812 kindless-legacy precedent: such an
/// entry is ambiguous between same-timeline and foreign, and dropping it would
/// regress the legit resumes that are the common case.
/// </summary>
internal readonly record struct TimelinePosition(int HighWaterSegment, TimelineIdentity? Identity);

/// <summary>
/// The TIMELINE IDENTITY a position's mark indexes (JF-787): the
/// (chapterCount, durationTicks) pair the JF-784 serve verdict uses, read off
/// the encode-metadata sidecar by the record gate (the single-item core stamps
/// its own degenerate pair instead: one chapter, the item's runtime). Both
/// numbers are DB counts/sums, so identities compare exactly.
/// </summary>
public readonly record struct TimelineIdentity(int ChapterCount, long DurationTicks);
