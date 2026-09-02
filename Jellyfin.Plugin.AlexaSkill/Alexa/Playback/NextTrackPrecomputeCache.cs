#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Per-device cache of pre-computed next-track data (JF-390). PlaybackStartedEventHandler
/// resolves the next queue item early (library metadata + stream URL) and stores it here;
/// PlaybackNearlyFinishedEventHandler checks this cache first and, on a hit, builds the
/// AudioPlayer.Play response without any library lookups (instant response on
/// high-latency endpoints). One live entry per device (Store replaces it): the entry
/// carries the current track's token and TryGet treats a mismatch as a miss, so an entry
/// stored while track A was playing can never be served for track B (JF-409: a stale
/// entry re-enqueued the currently playing track on itself). Entries are single-shot:
/// a successful TryGet consumes them, so one precomputed transition can only ever be
/// served once. Because the token is the BARE item GUID, a token match identifies an
/// item, not a playback session: the consumer (PlaybackNearlyFinishedEventHandler)
/// re-validates a served entry against the live session queue, and the
/// successor-DISPLACING paths call <see cref="Invalidate"/> (JF-424.1): ClearQueue,
/// PlayNext and ShuffleOn/ShuffleOff change which item follows the current one without
/// changing the current token, so the entry must be dropped eagerly. Pure appends
/// (e.g. AddToQueue at the tail) do not displace the successor of the currently playing
/// item and need no Invalidate; the serve-time successor check covers them.
/// </summary>
internal static class NextTrackPrecomputeCache
{
    private sealed record PrecomputedEntry(string CurrentTrackToken, Guid NextItemId, BaseItem Item, string StreamUrl, DateTimeOffset ComputedAt);

    private static readonly ConcurrentDictionary<string, PrecomputedEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a cache entry is valid before being considered stale. The TTL is evaluated
    /// lazily, only when TryGet reads the entry: there is no proactive sweep, so an entry
    /// for a device that stops playing stays resident until its next read. This is
    /// bounded and harmless: the dictionary is keyed by device, so it never holds more
    /// than one entry per device.
    /// </summary>
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Stores a pre-computed next-track entry for a device, replacing any previous entry
    /// (one live entry per device keeps the cache bounded).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="currentTrackToken">The token of the currently playing track (validated on read).</param>
    /// <param name="nextItemId">The resolved next queue item's ID.</param>
    /// <param name="item">The next item's library metadata (pre-fetched).</param>
    /// <param name="streamUrl">The pre-built stream URL for the next item.</param>
    public static void Store(string deviceId, string currentTrackToken, Guid nextItemId, BaseItem item, string streamUrl)
    {
        Cache[deviceId] = new PrecomputedEntry(currentTrackToken, nextItemId, item, streamUrl, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attempts to retrieve a pre-computed next-track entry for a device, valid only if
    /// the current track token matches and the entry is within its TTL. A valid
    /// retrieval consumes the entry (single-shot); a token mismatch leaves the stored
    /// entry intact (JF-424).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="currentTrackToken">The currently playing track's token (must match the stored entry).</param>
    /// <param name="nextItemId">The next item's ID if found.</param>
    /// <param name="item">The next item's metadata if found.</param>
    /// <param name="streamUrl">The pre-built stream URL if found.</param>
    /// <returns>True if a valid entry was found (and consumed).</returns>
    public static bool TryGet(string deviceId, string currentTrackToken, out Guid nextItemId, out BaseItem? item, out string? streamUrl)
    {
        nextItemId = Guid.Empty;
        item = null;
        streamUrl = null;

        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(currentTrackToken))
        {
            return false;
        }

        // Peek first (JF-424): removal happens only once the entry is actually served
        // or is dead, so a mismatched read cannot destroy the entry belonging to the
        // track now playing.
        if (!Cache.TryGetValue(deviceId, out PrecomputedEntry? entry))
        {
            return false;
        }

        // TTL check, before the token check: an expired entry is dead by definition
        // (ComputedAt is immutable, it can never re-enter the window), so it is
        // reclaimed on any read, matched or not; this is the only removal a mismatched
        // read may safely perform. The value-conditional remove takes only this exact
        // entry, never a concurrent Store's replacement.
        if (DateTimeOffset.UtcNow - entry.ComputedAt > EntryTtl)
        {
            Cache.TryRemove(new KeyValuePair<string, PrecomputedEntry>(deviceId, entry));
            return false;
        }

        // JF-409: the entry was stored while another track was playing; serving it here
        // re-enqueued the current track on itself on-device. Leave it stored (JF-424):
        // the mismatch marks a stale REQUEST (Amazon multi-fires NearlyFinished, so a
        // late duplicate for a track that already ended can arrive after PlaybackStarted
        // stored the entry for the track now playing), not a stale entry; removing it
        // here would send the real NearlyFinished down the full library+stream-URL
        // resolution path (the stall JF-390 exists to avoid). A within-TTL entry can
        // outlive its transition (TryPrecomputeNext Stores only when a next item
        // exists), but retention is bounded to one entry per device by the device
        // keying and to the TTL by the check above.
        if (!string.Equals(entry.CurrentTrackToken, currentTrackToken, StringComparison.Ordinal))
        {
            return false;
        }

        // Consume (single-shot): exactly one caller may serve a precomputed transition.
        // The value-conditional remove takes only this exact entry, never a concurrent
        // Store's replacement.
        if (!Cache.TryRemove(new KeyValuePair<string, PrecomputedEntry>(deviceId, entry)))
        {
            return false;
        }

        nextItemId = entry.NextItemId;
        item = entry.Item;
        streamUrl = entry.StreamUrl;
        return true;
    }

    /// <summary>
    /// Removes the cache entry for a device (e.g., when playback stops or the queue changes).
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static void Invalidate(string deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            Cache.TryRemove(deviceId, out _);
        }
    }
}
