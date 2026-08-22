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
/// high-latency endpoints). Keyed by (deviceId, currentTrackToken); the cache entry
/// becomes stale as soon as the current track changes, so each device only ever has
/// one live entry.
/// </summary>
internal static class NextTrackPrecomputeCache
{
    private sealed record PrecomputedEntry(Guid NextItemId, BaseItem Item, string StreamUrl, DateTimeOffset ComputedAt);

    private static readonly ConcurrentDictionary<string, PrecomputedEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a cache entry is valid before being considered stale. Generous: the entry
    /// is keyed to the currently playing track, so it naturally expires when the track
    /// changes. The TTL is a safety net for orphaned entries (e.g., if PlaybackStarted
    /// fires but PlaybackNearlyFinished never does because the user stopped playback).
    /// </summary>
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Stores a pre-computed next-track entry for a device, replacing any previous entry.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="currentTrackToken">The token of the currently playing track (the key qualifier).</param>
    /// <param name="nextItemId">The resolved next queue item's ID.</param>
    /// <param name="item">The next item's library metadata (pre-fetched).</param>
    /// <param name="streamUrl">The pre-built stream URL for the next item.</param>
    public static void Store(string deviceId, string currentTrackToken, Guid nextItemId, BaseItem item, string streamUrl)
    {
        Cache.AddOrUpdate(deviceId,
            _ => new PrecomputedEntry(nextItemId, item, streamUrl, DateTimeOffset.UtcNow),
            (_, _) => new PrecomputedEntry(nextItemId, item, streamUrl, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Attempts to retrieve a pre-computed next-track entry for a device, valid only if
    /// the current track token matches and the entry is within its TTL.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="currentTrackToken">The currently playing track's token (must match the entry's key).</param>
    /// <param name="nextItemId">The next item's ID if found.</param>
    /// <param name="item">The next item's metadata if found.</param>
    /// <param name="streamUrl">The pre-built stream URL if found.</param>
    /// <returns>True if a valid entry was found.</returns>
    public static bool TryGet(string deviceId, string currentTrackToken, out Guid nextItemId, out BaseItem? item, out string? streamUrl)
    {
        nextItemId = Guid.Empty;
        item = null;
        streamUrl = null;

        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(currentTrackToken))
        {
            return false;
        }

        if (!Cache.TryGetValue(deviceId, out PrecomputedEntry? entry))
        {
            return false;
        }

        // TTL check: stale entries are treated as misses (and cleaned up).
        if (DateTimeOffset.UtcNow - entry.ComputedAt > EntryTtl)
        {
            Cache.TryRemove(deviceId, out _);
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
