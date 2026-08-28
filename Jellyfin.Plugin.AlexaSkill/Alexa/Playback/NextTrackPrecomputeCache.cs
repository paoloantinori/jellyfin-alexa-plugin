#nullable enable
using System;
using System.Collections.Concurrent;
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
/// served once.
/// </summary>
internal static class NextTrackPrecomputeCache
{
    private sealed record PrecomputedEntry(string CurrentTrackToken, Guid NextItemId, BaseItem Item, string StreamUrl, DateTimeOffset ComputedAt);

    private static readonly ConcurrentDictionary<string, PrecomputedEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a cache entry is valid before being considered stale. The TTL is evaluated
    /// lazily, only when TryGet reads the entry: there is no proactive sweep, so an entry
    /// for a device that stops playing stays resident. This is bounded and harmless: Store
    /// replaces the single per-device entry on every track start, so the dictionary never
    /// holds more than one entry per device.
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
    /// the current track token matches and the entry is within its TTL. The retrieval
    /// always consumes the entry (single-shot), including on mismatch or expiry.
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

        // Consume on read (TryRemove): exactly one caller may serve a precomputed
        // transition, and an entry for a different track is stale by definition (the
        // device has moved on), so mismatches and orphans are reclaimed here too.
        if (!Cache.TryRemove(deviceId, out PrecomputedEntry? entry))
        {
            return false;
        }

        // JF-409: the entry was stored while another track was playing; serving it here
        // re-enqueued the current track on itself on-device.
        if (!string.Equals(entry.CurrentTrackToken, currentTrackToken, StringComparison.Ordinal))
        {
            return false;
        }

        // TTL check: stale entries are treated as misses.
        if (DateTimeOffset.UtcNow - entry.ComputedAt > EntryTtl)
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
