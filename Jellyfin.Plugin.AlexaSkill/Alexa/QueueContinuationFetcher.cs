using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Helper for fetching continuation batches from the Jellyfin library.
/// </summary>
internal static class QueueContinuationFetcher
{
    /// <summary>
    /// Disc-then-track ordering (ParentIndexNumber = disc, IndexNumber = track) for album-track
    /// queries (JF-339 AC#3). DB-level sort is required because the progressive queue paginates
    /// (initial page + continuation batches) — a per-page in-memory sort cannot keep global
    /// order, so every album-track site uses this and the pages concatenate consistently.
    /// Known edge: a NULL disc number sorts ahead of disc 1 on SQLite/MySQL (ASC NULLS FIRST)
    /// — cosmetic, only affects inconsistently-tagged albums, not fixable at this layer.
    /// </summary>
    internal static readonly (ItemSortBy, SortOrder)[] AlbumTrackOrder =
    {
        (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
        (ItemSortBy.IndexNumber, SortOrder.Ascending)
    };

    /// <summary>
    /// Renders a sentinel-capable total (a result total or a continuation total)
    /// for log lines: the end-unknown sentinel
    /// (<see cref="Util.SearchService.UnknownTotal"/>) renders "end-unknown" instead
    /// of a meaningless 2147483647. The ONE renderer: this dispatcher's lines, the
    /// album head's page logs, and the event handler's prefetch-window debug line
    /// share it (the JF-753 fold of the raw-TotalCount logs the JF-673 review
    /// tracked).
    /// </summary>
    /// <param name="totalCount">The total to render.</param>
    /// <returns>"end-unknown" or the total as invariant text.</returns>
    internal static string RenderTotal(int totalCount)
        => totalCount == Util.SearchService.UnknownTotal
            ? "end-unknown"
            : totalCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a fetched FIRST page carries no items, in BOTH total regimes (the
    /// JF-673 audiobook / JF-753 album head check; which arms carry the sentinel is
    /// the roster on <see cref="QueueContinuation.TotalCount"/>). Known-total: a
    /// zero <c>TotalRecordCount</c> is the server's "no rows at all". End-unknown
    /// (the opted-in NRE fallback reported
    /// <see cref="Util.SearchService.UnknownTotal"/>): the total is the sentinel,
    /// never a count, so the empty PAGE is the only meaningful zero signal.
    /// NOT the tail's split-album retry: that arm derives the regime from the
    /// CONTINUATION's total (the tail's own result total never carries the sentinel
    /// under the default-off executor), see FetchAlbumTracks.
    /// </summary>
    /// <param name="page">The first-page result to check.</param>
    /// <returns>True when the page carries no items in either regime.</returns>
    internal static bool PageHasNoItems(QueryResult<BaseItem> page)
        => page.TotalRecordCount == 0
           || (page.TotalRecordCount == Util.SearchService.UnknownTotal && page.Items.Count == 0);

    /// <summary>
    /// Whether a first page leaves more items to fetch, in BOTH total regimes (the
    /// JF-673 audiobook / JF-753 album continuation-store gate). Known-total: more
    /// rows exist beyond the page (<c>TotalRecordCount &gt; Items.Count</c>).
    /// End-unknown: no total exists to compare against, so a FULL initial page
    /// means "maybe more" (the stored sentinel keeps the tail fetching; a short
    /// page there ends the source, the FetchArtistSongs shape) and a short initial
    /// page means the source is already complete.
    /// </summary>
    /// <param name="page">The first-page result to judge.</param>
    /// <returns>True when a continuation should be stored for the rest.</returns>
    internal static bool InitialPageHasMore(QueryResult<BaseItem> page)
        => page.TotalRecordCount == Util.SearchService.UnknownTotal
            ? page.Items.Count >= ProgressiveQueueConstants.GetInitialFetchSize()
            : page.TotalRecordCount > page.Items.Count;

    /// <summary>
    /// The always-end-unknown form of
    /// <see cref="InitialPageHasMore(QueryResult{BaseItem})"/> (the artist heads:
    /// GetItemList has no count, so the full-page check IS the decision), so all
    /// four heads share the ONE maybe-more bar instead of hand-kept comparisons.
    /// </summary>
    /// <param name="fetchedCount">The number of items the first page served.</param>
    /// <returns>True when a full initial page leaves more to fetch.</returns>
    internal static bool InitialPageHasMore(int fetchedCount)
        => fetchedCount >= ProgressiveQueueConstants.GetInitialFetchSize();

    /// <summary>
    /// The known-total two-int form of <see cref="InitialPageHasMore(int)"/> for
    /// heads that hold the fetched page and its REAL total separately (the
    /// playlist head): the maybe-more bar stays the ONE named decision instead
    /// of a fifth hand-kept comparison in the minting file (the JF-753
    /// gate-marker's finding).
    /// </summary>
    /// <param name="fetchedCount">The number of items the first page served.</param>
    /// <param name="totalCount">The real total the server reported.</param>
    /// <returns>True when the total exceeds the fetched page.</returns>
    internal static bool InitialPageHasMore(int fetchedCount, int totalCount)
        => totalCount > fetchedCount;

    /// <summary>
    /// The ONE advance-or-mark idiom after a fetched continuation page (the JF-753
    /// consolidation of the three variants the JF-673 review tracked: album plain
    /// advance, artist mark-on-short, audiobook mark-only-when-end-unknown).
    /// Known-total continuations advance the offset; the entry guard ends them at
    /// <c>StartIndex &gt;= TotalCount</c>. End-unknown continuations
    /// (<c>TotalCount == <see cref="Util.SearchService.UnknownTotal"/></c>) have no
    /// total to exhaust against, so a SHORT page is the end signal: marking
    /// <c>StartIndex = TotalCount</c> makes the NEXT FetchNextBatch terminal via the
    /// entry guard instead of querying past the end and WARNing. Artist continuations
    /// always carry the sentinel (GetItemList has no count; both mint sites store it),
    /// so their historical mark-on-short behavior IS this helper unchanged.
    /// Accepted boundary noise (JF-673 review, same for albums JF-753): a source
    /// whose item count is an exact multiple of the page sizes ends on a ZERO-item
    /// tail batch, so the dispatcher's zero-page WARN fires once for a normally
    /// completed source, and an end-unknown ALBUM additionally pays one empty
    /// JF-338 AlbumIds retry query before the mark (FetchAlbumTracks fires the
    /// retry on the empty terminal page); the audiobook tail, which has no retry,
    /// carries only the WARN.
    /// </summary>
    /// <param name="continuation">The continuation whose offset the fetch advances.</param>
    /// <param name="fetchedCount">The number of items the fetch served.</param>
    private static void AdvanceOrMarkExhausted(QueueContinuation continuation, int fetchedCount)
    {
        if (continuation.TotalCount == Util.SearchService.UnknownTotal && fetchedCount < continuation.BatchSize)
        {
            continuation.StartIndex = continuation.TotalCount;
        }
        else
        {
            continuation.StartIndex += fetchedCount;
        }
    }

    /// <summary>
    /// Fetch the next batch of items based on continuation data.
    /// Updates the continuation's StartIndex after fetching.
    /// </summary>
    /// <param name="continuation">The continuation state.</param>
    /// <param name="libraryManager">The library manager for queries.</param>
    /// <param name="userManager">The user manager for resolving Jellyfin users.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="pluginUser">The plugin user whose library scope the batch must respect, mirroring the initial fetches (null: unrestricted).</param>
    /// <returns>The fetched items, or empty list if no more items.</returns>
    public static IReadOnlyList<BaseItem> FetchNextBatch(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger logger,
        Entities.User? pluginUser = null)
    {
        if (continuation.StartIndex >= continuation.TotalCount)
        {
            return Array.Empty<BaseItem>();
        }

        var jellyfinUser = userManager.GetUserById(continuation.UserId);

        // Captured before the fetchers advance StartIndex: the zero-page diagnostic
        // below must name the offset the query ran at (an end-unknown fetcher marks
        // StartIndex=TotalCount on a short page, erasing it before the log).
        int queryOffset = continuation.StartIndex;

        // One switch owns the dispatch, the source-id label, and (via a null label)
        // the zero-page anomaly gate: null marks the default arm (an unknown source
        // type), whose empty page is structural, not a library anomaly. Listed arms
        // carry labels, so their zero pages WARN; the Audiobook arm joined them in
        // JF-670 (its structural zero used to truncate books at the initial page,
        // which is exactly why a real empty page here must be loud). Known accepted
        // noise: the Playlist cached-slice arm can zero out on a stale snapshot
        // (playlist shrunk since first-play) and WARNs too; the one-dimensional
        // label cannot tell that apart from a real fetch failure, and either way
        // the caller's store Remove makes it terminal.
        (IReadOnlyList<BaseItem> Items, string? SourceId) fetched = continuation.SourceType switch
        {
            "Album" => (FetchAlbumTracks(continuation, libraryManager, jellyfinUser, pluginUser, logger), $"album {continuation.ParentId}"),
            "Artist" => (FetchArtistSongs(continuation, libraryManager, jellyfinUser, pluginUser, logger), $"artist {continuation.ArtistId}"),
            "Playlist" => (FetchPlaylistItems(continuation, libraryManager, jellyfinUser), $"playlist {continuation.PlaylistId ?? continuation.ParentId}"),
            "Audiobook" => (FetchAudiobookChapters(continuation, libraryManager, jellyfinUser, pluginUser, logger), $"book {continuation.ParentId}"),
            _ => (Array.Empty<BaseItem>(), null)
        };

        // The continuation total may be the end-unknown sentinel (which arms carry
        // it is the roster on QueueContinuation.TotalCount); render it honestly
        // through the ONE renderer (see RenderTotal).
        string totalText = RenderTotal(continuation.TotalCount);

        if (fetched.Items.Count > 0)
        {
            logger.LogInformation(
                "Progressive queue: fetched {Count} items for {SourceType} (offset {StartIndex}/{Total})",
                fetched.Items.Count,
                continuation.SourceType,
                queryOffset,
                totalText);
        }
        else if (fetched.SourceId != null)
        {
            // Here StartIndex < TotalCount (the guard above owns the exhausted state)
            // yet the query returned nothing.
            logger.LogWarning(
                "Progressive queue: fetched 0 items for {SourceType} {SourceId} (offset {StartIndex}/{Total}); treating continuation as exhausted",
                continuation.SourceType,
                fetched.SourceId,
                queryOffset,
                totalText);
        }

        return fetched.Items;
    }

    private static IReadOnlyList<BaseItem> FetchAlbumTracks(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User? pluginUser,
        ILogger logger)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = continuation.ParentId ?? Guid.Empty,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true),
            OrderBy = AlbumTrackOrder,
            StartIndex = continuation.StartIndex,
            Limit = continuation.BatchSize
        };

        // Same per-user library scope the initial album fetch runs under
        // (AlbumPlayService.BuildAlbumQuery); without it a continuation batch
        // widens the scope to every library the Jellyfin account sees.
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);

        // Shared executor guard (the JF-670 head/tail contract the audiobook tail
        // already runs under, extended to the album tail by JF-753): the album head
        // survives NRE-class servers through SafeGetItemsResult, so the tail must
        // too or the first continuation batch would die on the same query shape the
        // head fell back on. Default-off opt-in: the tail never reads the fallback
        // total as a count (the regime rides on the continuation), and the
        // split-album retry's known-total arm keeps reading real totals.
        QueryResult<BaseItem> result = Util.SearchService.SafeGetItemsResult(libraryManager, query, logger);

        // Tolerant fallback: for split / multi-disc / malformed-folder albums the
        // folder-based ParentId query returns 0 (PlayAlbumIntentHandler's initial
        // fetch retries by AlbumIds for the same reason, JF-338). Mirror that here
        // or progressive continuation truncates the album to the initial page.
        // JF-753 end-unknown arm: in that regime the result total is the fallback's
        // page size (or the sentinel), never a library count, so the EMPTY PAGE is
        // the retry trigger; TotalRecordCount == 0 stays meaningful only in the
        // known-total regime.
        // RESIDUAL (code-review JF-753, accepted): on a PARTIALLY split album this
        // retry can switch row sets mid-stream (the head served ParentId rows; the
        // retry then serves AlbumIds rows at the same offset, a different
        // subsequence when the two link sets diverge). Already-played ids dedup out
        // (SessionQueue.AppendUnseen) and tag-linked tracks ordered below the
        // offset can be skipped. Continuing beats the alternative (truncating at
        // the exhausted parented rows), and the head carries the same JF-338
        // tolerance: it retries only on an empty FIRST page, so a partially split
        // album's tag-linked tail is invisible there too.
        if (continuation.ParentId.HasValue
            && (result.TotalRecordCount == 0
                || (continuation.TotalCount == Util.SearchService.UnknownTotal && result.Items.Count == 0)))
        {
            var albumIdsQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                AlbumIds = new[] { continuation.ParentId.Value },
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                DtoOptions = new DtoOptions(true),
                OrderBy = AlbumTrackOrder,
                StartIndex = continuation.StartIndex,
                Limit = continuation.BatchSize
            };
            Util.LibraryFilter.ApplyLibraryFilter(albumIdsQuery, pluginUser, libraryManager, logger);
            result = Util.SearchService.SafeGetItemsResult(libraryManager, albumIdsQuery, logger);
        }

        // JF-753: the ONE advance-or-mark idiom (see AdvanceOrMarkExhausted).
        AdvanceOrMarkExhausted(continuation, result.Items.Count);
        return result.Items;
    }

    /// <summary>
    /// The ONE audiobook chapters-page query (JF-670): the head (PlayBookIntentHandler
    /// initial page) and the tail (continuation batches) MUST share one query shape so
    /// the pages concatenate in one deterministic order. This builder is the single
    /// definition; a hand-kept copy in the handler would drift exactly like the
    /// stale-mirror class that bit the interaction-model docs (anti-pattern 11).
    /// Deliberately MediaTypes, not the JF-358 IncludeItemTypes discipline: JF-358
    /// governs ArtistIds queries, which MediaTypes silently ignores; a ParentId query
    /// IS constrained by MediaTypes (this shape returns the chapters in production).
    /// Deliberately NO OrderBy: the DB order for this shape IS the book's chapter
    /// order; an album-style disc/track sort here would order the tail differently
    /// from the head.
    /// </summary>
    /// <param name="jellyfinUser">The query user (session scope).</param>
    /// <param name="bookId">The book folder ID (ParentId scope).</param>
    /// <param name="startIndex">Page offset (0 for the initial page).</param>
    /// <param name="limit">Page size.</param>
    /// <returns>The query (executor differs by site: the handler wraps in
    /// SafeGetItemsResult, the fetcher's guard mirrors it, see FetchAudiobookChapters).</returns>
    internal static InternalItemsQuery BuildAudiobookChaptersQuery(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid bookId,
        int startIndex,
        int limit)
        => new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = bookId,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            StartIndex = startIndex,
            Limit = limit
        };

    private static IReadOnlyList<BaseItem> FetchAudiobookChapters(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User? pluginUser,
        ILogger logger)
    {
        // Shared builder with PlayBookIntentHandler's initial page (JF-670): one query
        // shape owns head and tail, see the builder's doc.
        InternalItemsQuery query = BuildAudiobookChaptersQuery(
            jellyfinUser,
            continuation.ParentId ?? Guid.Empty,
            continuation.StartIndex,
            continuation.BatchSize);

        // Same per-user library scope the other continuation fetchers run under
        // (JF-666 parity). The book folder itself was resolved under the same filter
        // at PlayBook time, so its chapters cannot be scoped out; the parity matters
        // for restricted-library accounts, not for this folder's row membership.
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);

        // Shared executor guard (JF-670 review): the initial page runs this same
        // query through SearchService.SafeGetItemsResult; head and tail share the
        // ONE static core so the NRE-to-GetItemList fallback cannot drift between
        // them (catch class, log wording, fallback shape). Head surviving while
        // the tail dies would be the same mid-book truncation this arm exists to
        // fix.
        QueryResult<BaseItem> result = Util.SearchService.SafeGetItemsResult(libraryManager, query, logger);

        // JF-673: the ONE advance-or-mark idiom (see AdvanceOrMarkExhausted).
        AdvanceOrMarkExhausted(continuation, result.Items.Count);

        return result.Items;
    }

    private static IReadOnlyList<BaseItem> FetchArtistSongs(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User? pluginUser,
        ILogger logger)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            // JF-358/JF-666: filter via IncludeItemTypes=Audio, NOT MediaTypes=Audio.
            // MediaTypes does not constrain an ArtistIds query; on the direct
            // ILibraryManager path it returned ZERO items (live 2026-09-29: offset 5
            // of a 13-track artist), silently marking the continuation exhausted.
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true),
            ArtistIds = continuation.ArtistId.HasValue ? new[] { continuation.ArtistId.Value } : Array.Empty<Guid>(),
            StartIndex = continuation.StartIndex,
            Limit = continuation.BatchSize
        };

        // Same per-user library scope the initial artist fetch runs under
        // (SearchService.GetArtistSongsAsync).
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);

        if (continuation.SortOrder != null)
        {
            query.OrderBy = continuation.SortOrder;
        }

        // Use GetItemList instead of GetItemsResult to avoid Jellyfin NRE in
        // dbQuery.Count() when ArtistIds + PopularitySort expressions are combined.
        IReadOnlyList<BaseItem> items = libraryManager.GetItemList(query);

        // End of results: the ONE advance-or-mark idiom (artist arms are always
        // end-unknown; see AdvanceOrMarkExhausted).
        AdvanceOrMarkExhausted(continuation, items.Count);

        return items;
    }

    private static IReadOnlyList<BaseItem> FetchPlaylistItems(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        // Cached path (issue #10 efficiency): the handler already resolved the full audio
        // track list at first-play. Slice it here instead of re-resolving every linked child
        // via GetManageableItems() on each PlaybackNearlyFinished (which is O(playlist size)
        // per batch). Order is stable because both the handler and this slice operate on the
        // same cached list.
        if (continuation.CachedTracks is { Count: > 0 } cached)
        {
            var batch = cached
                .Skip(continuation.StartIndex)
                .Take(continuation.BatchSize)
                .ToList();

            // Known-total cached slice: the ONE idiom's advance branch (see
            // AdvanceOrMarkExhausted; playlist totals are always real counts).
            AdvanceOrMarkExhausted(continuation, batch.Count);
            return batch;
        }

        // Fallback (no cache): resolve on demand. Only reached for a Playlist continuation
        // created without CachedTracks. Playlist members are linked children, NOT
        // ParentId-owned rows — querying ILibraryManager with ParentId=playlist.Id always
        // returns 0 (issue #10) — so resolve via PlaylistTrackResolver.
        if (continuation.PlaylistId == null)
        {
            return Array.Empty<BaseItem>();
        }

        BaseItem? playlist = libraryManager.GetItemById(continuation.PlaylistId.Value);
        if (playlist is not Playlist playlistEntity)
        {
            return Array.Empty<BaseItem>();
        }

        IReadOnlyList<BaseItem> allTracks = PlaylistTrackResolver.GetAudioTracks(playlistEntity, jellyfinUser);

        var fallback = allTracks
            .Skip(continuation.StartIndex)
            .Take(continuation.BatchSize)
            .ToList();

        AdvanceOrMarkExhausted(continuation, fallback.Count);
        return fallback;
    }
}
