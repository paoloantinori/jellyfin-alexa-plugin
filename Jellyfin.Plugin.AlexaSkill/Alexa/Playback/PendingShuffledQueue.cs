using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// JF-713 derive-then-commit: a DERIVED but uncommitted shuffled order. The
/// playlist shuffle arm of <c>AlbumPlayService.BuildPlaylistPlayResponseAsync</c>
/// derives the shuffle ONCE (the first-played track, and so the launch build,
/// is picked from this snapshot), then commits it via
/// <see cref="DeviceQueueManager.CommitShuffledQueue"/> only after the launch
/// build succeeds, so a refused launch leaves the device queue untouched (the
/// JF-699 residual queue-write-before-the-build shape this record closes).
/// Committing MUST reuse the snapshot, never re-derive: a second derivation would
/// re-shuffle and disagree with the launch the device actually received.
/// READ-ONLY surface (the JF-713 code-review finding): the lists are exposed as
/// <see cref="IReadOnlyList{T}"/> and <see cref="DeviceQueueManager.CommitShuffledQueue"/>
/// stores defensive copies, so neither a post-commit mutation of the snapshot nor
/// a second commit of it can reach a live queue's mutable state.
    /// CONVENTION-ONLY CONTRACT (JF-713 gate-marker): nothing structural ties the
    /// committed snapshot to the launch that was built from it (the record's
    /// non-emptiness contract has the FirstItemId guard; this one does not). The
    /// sole caller derives once and commits the same local; if a second commit
    /// caller ever appears, give this a structural guard (or route the launch
    /// build through the record so the pair cannot diverge) rather than relying
    /// on this note - a divergent pair reintroduces exactly the
    /// directive-versus-queue disagreement JF-713 fixed. The JF-712
    /// PendingContinuation idiom shares this accepted shape.
/// </summary>
/// <param name="ShuffledItemIds">The shuffled playback order (never empty at a correct derive site).</param>
/// <param name="OriginalItemIds">The pre-shuffle order, stored on commit for RestoreOrder.</param>
public sealed record PendingShuffledQueue(IReadOnlyList<string> ShuffledItemIds, IReadOnlyList<string> OriginalItemIds)
{
    /// <summary>
    /// Gets the first shuffled item's id: the item the launch builds. Structural
    /// guard (the JF-712 PendingContinuation idiom): the derive site must never
    /// shuffle an empty list (its caller answered the PlaylistEmpty tell first),
    /// so an empty list here is a construction-site bug and fails with the
    /// contract named, not a distant index error.
    /// </summary>
    public string FirstItemId => ShuffledItemIds.Count > 0
        ? ShuffledItemIds[0]
        : throw new InvalidOperationException("PendingShuffledQueue was constructed with an empty id list; the derive site must not shuffle an empty playlist (JF-713)");
}
