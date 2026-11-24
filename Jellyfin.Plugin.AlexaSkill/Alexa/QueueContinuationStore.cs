using System;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// In-memory store for queue continuation state, keyed by user ID and device ID.
/// Follows the same pattern as <see cref="RadioModeState"/>.
/// </summary>
internal static class QueueContinuationStore
{
    private static readonly ConcurrentDictionary<string, QueueContinuation> _store = new();

    private static string Key(Guid userId, string deviceId)
        => $"{userId}:{deviceId}";

    /// <summary>
    /// Store continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="continuation">The continuation data to store.</param>
    public static void Set(Guid userId, string deviceId, QueueContinuation continuation)
        => _store[Key(userId, deviceId)] = continuation;

    /// <summary>
    /// Retrieve continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>The continuation data, or null if none exists.</returns>
    public static QueueContinuation? Get(Guid userId, string deviceId)
        => _store.TryGetValue(Key(userId, deviceId), out var continuation) ? continuation : null;

    /// <summary>
    /// Remove continuation data for a given user/device.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    public static void Remove(Guid userId, string deviceId)
        => _store.TryRemove(Key(userId, deviceId), out _);

    /// <summary>
    /// JF-928: update the stored continuation's per-batch shuffle semantics
    /// (<see cref="QueueContinuation.Shuffle"/>) for the CURRENT live queue. The
    /// mid-playback ShuffleOn/Off channel: without it, a shuffle-on over an
    /// ORDERED windowed play leaves the entry ordered, and when the shuffled
    /// window drains the fetcher appends the rest of the source in source order
    /// (the JF-927 defect class via the ShuffleOn entry point).
    /// <see cref="QueueContinuation"/> is init-only, so the flip is a re-mint
    /// (<see cref="QueueContinuation.WithShuffleSemantics"/>), never a mutation.
    /// Keyed on the JF-674 live-queue identity gate: a continuation whose minted
    /// page is no longer a subset of the live queue belongs to an earlier
    /// playback and is never mutated here (the fetch-time guard owns its
    /// discard). A no-entry device (short queue, a fully-consumed source) is a
    /// no-op.
    /// The shuffle-semantics flip is a MUSIC-continuation decision: the Audiobook
    /// source is deliberately EXCLUDED (this method returns false for it, documented
    /// here as the one owner of the rule). A book's chapters are an ordered read;
    /// pre-JF-928 no path ever set Shuffle on an Audiobook entry, the fetcher's
    /// ShuffleCopy gates on nothing but the flag, and the JF-674 doc keeps a live
    /// Audiobook continuation on the AudioPlayer path (screenless chapter resume)
    /// where the shuffle twins' medium gate reads Audio and passes. Without this
    /// exclusion a mid-book "shuffle on" would scatter the remaining chapters.
    /// KNOWN RACE (the JF-713 documented-boundary precedent): the re-mint is a
    /// non-atomic Get-clone-Set while the fetch path mutates the stored instance's
    /// offsets IN PLACE (AdvanceOrMarkExhausted), so a NearlyFinished fetch landing
    /// inside the window can have its advance lost to the Set. The consequences are
    /// bounded: the next fetch re-slices the same cached range, and AppendUnseen's
    /// id-set dedupe appends nothing for already-queued tracks, so there are never
    /// duplicate queue entries. The race can ALSO LOSE THE FLIP ITSELF: a fetch that
    /// Got the entry before the Set reads the stale instance at the ShuffleCopy
    /// gate, and one batch appends in source order against a just-commanded
    /// shuffle-on (the command appears not to work for that batch; the NEXT batch
    /// picks up the flipped entry and self-heals). A lock would have to
    /// cover the fetcher's own read-mutate (outside this method), which the
    /// per-batch design does not need today; revisit only if a consumer takes a
    /// reference-equality or exact-offset dependency on the stored instance.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID (the store key).</param>
    /// <param name="deviceId">The Alexa device ID (the store key).</param>
    /// <param name="session">The session whose now-playing queue is the live queue.</param>
    /// <param name="shuffle">The new per-batch shuffle semantics.</param>
    /// <returns>True when a live MUSIC continuation was found and now carries
    /// <paramref name="shuffle"/> semantics (re-minted, or already carrying
    /// them); false when no entry exists, the entry is foreign to this queue,
    /// or the entry is an Audiobook continuation (ordered chapters are never
    /// scattered). True does NOT promise a new stored instance: the idempotent
    /// path leaves the original in place, so never take a reference-equality
    /// dependency on the return.</returns>
    public static bool TryUpdateShuffleForLiveQueue(Guid userId, string deviceId, SessionInfo session, bool shuffle)
    {
        QueueContinuation? continuation = Get(userId, deviceId);
        if (continuation == null)
        {
            return false;
        }

        // JF-674 identity gate: the same check the fetch guard runs, so the
        // update and the fetch can never disagree about which queue an entry
        // belongs to.
        if (!continuation.IsForLiveQueue(session))
        {
            return false;
        }

        // The music-source gate (see the doc above for the full rationale): only
        // the three playlist-shaped sources flip. An Audiobook continuation stays
        // ordered forever; a future fourth source type must add itself here
        // EXPLICITLY, so a new source cannot silently inherit shuffle semantics.
        if (continuation.SourceType is not ("Playlist" or "Album" or "Artist"))
        {
            return false;
        }

        // Idempotent short-circuit: a re-command of the semantics the entry
        // already carries skips the re-mint and the store rewrite.
        if (continuation.Shuffle == shuffle)
        {
            return true;
        }

        Set(userId, deviceId, continuation.WithShuffleSemantics(shuffle));
        return true;
    }

    /// <summary>
    /// Remove all entries for a given user (cleanup on session end).
    /// </summary>
    /// <param name="userId">The user ID whose entries should be removed.</param>
    public static void RemoveAllForUser(Guid userId)
    {
        string prefix = $"{userId}:";
        foreach (var kvp in _store)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _store.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Clear all entries. For test teardown only.
    /// </summary>
    internal static void Clear() => _store.Clear();
}
