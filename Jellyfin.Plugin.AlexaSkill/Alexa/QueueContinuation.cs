using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Continuation state for progressive queue building.
/// Stores enough information to fetch the next batch of items on demand,
/// enabling fast time-to-first-audio by only fetching an initial page.
/// </summary>
public class QueueContinuation
{
    /// <summary>
    /// Gets the source type for the continuation query.
    /// </summary>
    public string SourceType { get; init; } = string.Empty;

    /// <summary>
    /// Gets the parent ID (album ID for album tracks, playlist ID for playlist items).
    /// </summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Gets the artist ID (for artist songs queries).
    /// </summary>
    public Guid? ArtistId { get; init; }

    /// <summary>
    /// Gets the playlist ID (for playlist item queries).
    /// </summary>
    public Guid? PlaylistId { get; init; }

    /// <summary>
    /// Gets or sets the next offset to fetch from the result set.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the total number of items available in the full result set.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items to fetch per continuation batch.
    /// </summary>
    public int BatchSize { get; set; } = ProgressiveQueueConstants.GetContinuationBatchSize();

    /// <summary>
    /// Gets the Jellyfin user ID for the query.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Gets the sort order used for the original query (to maintain consistency across batches).
    /// </summary>
    public (ItemSortBy SortBy, SortOrder Order)[]? SortOrder { get; init; }

    /// <summary>
    /// Gets a value indicating whether continuation batches should be shuffled
    /// before being appended to the queue.
    /// </summary>
    public bool Shuffle { get; init; }

    /// <summary>
    /// Gets the fully-resolved playlist tracks (audio + visible, in stable playlist order),
    /// cached at first-play so continuation batches can slice this list instead of
    /// re-resolving every linked child via <c>Playlist.GetManageableItems()</c> on each
    /// <c>PlaybackNearlyFinished</c>. Null for Album/Artist sources, which use DB-level
    /// pagination. Holds references to Jellyfin-cached <see cref="BaseItem"/>s, so holding
    /// the list allocates no new objects (the items already live in the LibraryManager cache).
    /// The store is keyed by user+device and overwritten on each new play, so at most one
    /// continuation per device; it is removed when the queue exhausts (PlaybackNearlyFinished)
    /// — NOT on PlaybackStopped, so a stopped-but-not-exhausted playlist lingers until the
    /// next playback overwrites it. Bounded and negligible memory.
    /// </summary>
    public IReadOnlyList<BaseItem>? CachedTracks { get; init; }

    /// <summary>
    /// JF-674 queue identity: the item ids of the queue page this continuation was
    /// minted over (the exact ids the minting play installed into the session's
    /// now-playing queue, captured through <see cref="QueueIdsOf"/>). The
    /// fetch-time validation (<see cref="IsForLiveQueue"/>) binds the entry to
    /// THAT queue: a later playback whose queue does not contain every minted id
    /// is a different logical queue, and the entry is discarded instead of
    /// injecting its source's content after the later play (the filed scenario:
    /// a stale Audiobook continuation appending mid-book chapters after a fresh
    /// single-song play). SET membership, not order or full-list equality, is
    /// the identity relation: the fetch's own appends, AddToQueue/PlayNext
    /// inserts at any position, shuffle mirrors, and the JF-574 rehydration all
    /// keep the minted page inside the live queue, while every resume-path
    /// ReplaceAll relaunch leaves the queue untouched. An EMPTY list means NO
    /// identity was captured and validation is skipped: the hand-constructed
    /// test shape, or a mint whose installed page slice is itself empty (no
    /// current producer returns a startIndex equal to the page count, so every
    /// production capture is non-empty; the roster cannot see that case, it
    /// guards only that the initializer is present). Production wiring is
    /// enforced structurally by QueueContinuationIdentityRosterTests (every
    /// plugin-assembly construction site must initialize this property).
    /// </summary>
    public IReadOnlyList<Guid> MintedQueueItemIds { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// JF-674: whether the live session queue is still the queue this continuation
    /// was minted for, i.e. every <see cref="MintedQueueItemIds"/> entry is still
    /// queued (membership through <see cref="SessionQueue.IdSet"/>, the ONE
    /// session-queue membership source). Empty identity short-circuits true (see
    /// the property doc). Called by the fetch guard in
    /// <c>PlaybackNearlyFinishedEventHandler.TryFetchContinuationBatch</c> between
    /// the current-index guard and the threshold guard; a mismatch discards the
    /// store entry instead of fetching.
    /// </summary>
    /// <param name="session">The session whose now-playing queue is the live queue.</param>
    /// <returns>True when the entry may serve this queue (or carries no identity).</returns>
    public bool IsForLiveQueue(SessionInfo session)
    {
        if (MintedQueueItemIds.Count == 0)
        {
            return true;
        }

        HashSet<Guid> liveIds = SessionQueue.IdSet(session);
        foreach (Guid mintedId in MintedQueueItemIds)
        {
            if (!liveIds.Contains(mintedId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// JF-674: the ONE projection a mint site uses to capture its queue page as the
    /// continuation's identity (the five creators pass the exact queue-items list they
    /// install into <c>session.NowPlayingQueue</c>), so the capture shape cannot drift
    /// per site.
    /// </summary>
    /// <param name="queueItems">The queue page being installed at mint time.</param>
    /// <returns>The page's item ids, in queue order (identity is set-shaped; order is incidental).</returns>
    public static IReadOnlyList<Guid> QueueIdsOf(IEnumerable<QueueItem> queueItems)
        => queueItems.Select(q => q.Id).ToList();
}
