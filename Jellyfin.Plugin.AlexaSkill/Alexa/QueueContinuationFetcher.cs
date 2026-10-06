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
        // Shared builder with the album head's first page (JF-757): one query shape
        // owns head and tail, both arms, see BuildAlbumTracksQuery's doc.
        var query = BuildScopedAlbumTracksQuery(
            jellyfinUser, pluginUser, libraryManager, logger,
            continuation.ParentId ?? Guid.Empty,
            continuation.StartIndex,
            continuation.BatchSize,
            byAlbumIds: false);

        // Same per-user library scope the initial album fetch runs under
        // (AlbumPlayService.BuildAlbumQuery); without it a continuation batch
        // widens the scope to every library the Jellyfin account sees.

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
            var albumIdsQuery = BuildScopedAlbumTracksQuery(
                jellyfinUser, pluginUser, libraryManager, logger,
                continuation.ParentId.Value,
                continuation.StartIndex,
                continuation.BatchSize,
                byAlbumIds: true);
            result = Util.SearchService.SafeGetItemsResult(libraryManager, albumIdsQuery, logger);
        }

        // JF-753: the ONE advance-or-mark idiom (see AdvanceOrMarkExhausted).
        AdvanceOrMarkExhausted(continuation, result.Items.Count);
        return result.Items;
    }

    /// <summary>
    /// The ONE scoped album-tracks page builder (JF-763 gate-marker): builds via
    /// <see cref="BuildAlbumTracksQuery"/> then applies
    /// <c>Util.LibraryFilter.ApplyLibraryFilter</c> in the same call, so
    /// the JF-666 head/tail scope-parity pairing is structural at every paged
    /// site instead of a hand-repeated two-line convention a fifth site can
    /// forget. The executors stay at the callers (RetryAsync wrap vs direct).
    /// </summary>
    internal static InternalItemsQuery BuildScopedAlbumTracksQuery(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        AlexaSkill.Entities.User? pluginUser,
        ILibraryManager libraryManager,
        ILogger logger,
        Guid albumId,
        int startIndex,
        int limit,
        bool byAlbumIds)
    {
        InternalItemsQuery query = BuildAlbumTracksQuery(jellyfinUser, albumId, startIndex, limit, byAlbumIds);
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);
        return query;
    }

    /// <summary>
    /// The ONE album-tracks query of the paged playback path (JF-757): the album
    /// head's first page (both the ParentId arm and the JF-338 AlbumIds retry in
    /// AlbumPlayService.BuildAlbumPlayResponseAsync) and the album tail's
    /// continuation batches (the same two arms in <see cref="FetchAlbumTracks"/>)
    /// MUST share one query shape so the pages concatenate in one deterministic
    /// order (the JF-670 head/tail contract; the audiobook twin is
    /// <see cref="BuildAudiobookChaptersQuery"/>). This builder replaced the four
    /// hand-kept initializers, which would drift exactly like the stale-mirror
    /// class that bit the interaction-model docs (anti-pattern 11).
    /// JF-763 closed the former accepted boundary: the album-concat endpoint
    /// (VideoAudioController) also routes its two isMusicAlbum arms through this
    /// shape via the unpaged form (<see cref="BuildAlbumTracksQueryUnpaged"/>),
    /// so field set, arms, and order are definitionally shared. JF-767 closed the
    /// row-set residual that JF-763 filed: the endpoint applies the library scope
    /// the JF-309 token now carries (minted from the launching user's
    /// AllowedLibraryIds at the URL builder), so the concat enumeration runs under
    /// the SAME per-user scope the paged path applies (JF-666 parity) and the
    /// seek-mode resume slice AlbumPlayService sums over its scoped rows lands on
    /// the timeline the endpoint encodes. Tokens minted before that change (or by
    /// an unrestricted user) carry no scope and keep enumerating unscoped, the
    /// pre-JF-767 behavior.
    /// Deliberately IncludeItemTypes=Audio (the JF-358 discipline), unlike the
    /// audiobook builder's MediaTypes: a ParentId/AlbumIds query IS constrained by
    /// IncludeItemTypes and this shape returns the tracks in production. The
    /// scoping term is the ONE arm difference: ParentId = folder children,
    /// AlbumIds = membership by album tag (the split/malformed-folder retry,
    /// JF-338). Paging is the caller's (the head passes 0 +
    /// GetInitialFetchSize, the tail StartIndex + BatchSize; the endpoint and the
    /// YesIntent confirm pass none). The UNSELECTED scoping field stays at its
    /// constructor default: an explicitly set ParentId on the membership arm (or
    /// AlbumIds on the folder arm) would AND a second constraint into the server
    /// query and break the retry's recovery.
    /// </summary>
    /// <param name="jellyfinUser">The query user (session scope).</param>
    /// <param name="albumId">The album ID, scoped by the chosen arm.</param>
    /// <param name="startIndex">Page offset (0 for the initial page).</param>
    /// <param name="limit">Page size.</param>
    /// <param name="byAlbumIds">True for the JF-338 membership arm (AlbumIds),
    /// false for the folder arm (ParentId).</param>
    /// <returns>The query (executor and per-user library filtering differ by site:
    /// head and tail both apply ApplyLibraryFilter then SafeGetItemsResult, the
    /// JF-763 head/tail scope parity; the unpaged endpoint form reads the scope
    /// off the stream token instead, having no session user).</returns>
    internal static InternalItemsQuery BuildAlbumTracksQuery(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid albumId,
        int startIndex,
        int limit,
        bool byAlbumIds)
        => BuildAlbumTracksQueryCore(jellyfinUser, albumId, startIndex, limit, byAlbumIds);

    /// <summary>
    /// The unpaged form of <see cref="BuildAlbumTracksQuery"/>: the album-concat
    /// endpoint (VideoAudioController, JF-763) passes <c>jellyfinUser: null</c>
    /// (that HTTP path is token-gated with no session user; its library scope now
    /// arrives on the token, JF-767), while the YesIntent MusicAlbum confirm
    /// (JF-767 Finding A) passes the session user. Enumerates the whole album in
    /// one GetItemList call (paging stays null: fetch-all; null is the SDK's
    /// no-paging value, NOT 0, which is Take(0) per JF-443). The row-set
    /// rationale and the field set live on the ONE core (see
    /// <see cref="BuildAlbumTracksQuery"/>'s doc).
    /// </summary>
    /// <param name="jellyfinUser">The query user (session scope), or null on the
    /// token-gated endpoint path.</param>
    /// <param name="albumId">The album ID, scoped by the chosen arm.</param>
    /// <param name="byAlbumIds">True for the JF-338 membership arm (AlbumIds),
    /// false for the folder arm (ParentId).</param>
    /// <returns>The unpaged query.</returns>
    internal static InternalItemsQuery BuildAlbumTracksQueryUnpaged(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid albumId,
        bool byAlbumIds)
        => BuildAlbumTracksQueryCore(jellyfinUser, albumId, startIndex: null, limit: null, byAlbumIds);

    /// <summary>
    /// The unpaged, session-user sibling of
    /// <see cref="BuildScopedAlbumTracksQuery"/> (JF-767 Finding A): builds via
    /// <see cref="BuildAlbumTracksQueryUnpaged"/> then applies
    /// <c>Util.LibraryFilter.ApplyLibraryFilter</c> in the same call, so the
    /// JF-666 scope pairing is structural at the YesIntent MusicAlbum confirm the
    /// same way it is at every paged site (a hand-repeated two-line convention a
    /// sixth site can forget). The executors stay at the callers.
    /// </summary>
    internal static InternalItemsQuery BuildScopedAlbumTracksQueryUnpaged(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        AlexaSkill.Entities.User? pluginUser,
        ILibraryManager libraryManager,
        ILogger logger,
        Guid albumId,
        bool byAlbumIds)
    {
        InternalItemsQuery query = BuildAlbumTracksQueryUnpaged(jellyfinUser, albumId, byAlbumIds);
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);
        return query;
    }

    /// <summary>
    /// The ONE field-set owner both album-tracks entry points share (paged and
    /// unpaged); a field added here reaches every consumer of the shape.
    /// </summary>
    private static InternalItemsQuery BuildAlbumTracksQueryCore(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid albumId,
        int? startIndex,
        int? limit,
        bool byAlbumIds)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true),
            OrderBy = AlbumTrackOrder,
            // Nullable in the SDK: null = no paging (the unpaged form), 0 = Take(0)
            // (JF-443), so the int? params pass through untouched.
            StartIndex = startIndex,
            Limit = limit
        };

        if (byAlbumIds)
        {
            query.AlbumIds = new[] { albumId };
        }
        else
        {
            query.ParentId = albumId;
        }

        return query;
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
    /// JF-784 leg 3 closed the kind-axis divergence this shape's consumers had:
    /// the concat endpoint's audiobook arm now routes through the unpaged form
    /// (<see cref="BuildAudiobookChaptersQueryUnpaged"/>) instead of its local
    /// IncludeItemTypes=AudioBook initializer, so the endpoint enumerates the
    /// SAME rows the head/confirm/tail queue (MediaTypes=Audio also matches
    /// Audio-typed chapters, the metadata-remap shape the AudioBook-kind filter
    /// 404ed).
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
        => BuildAudiobookChaptersQueryCore(jellyfinUser, bookId, startIndex, limit);

    /// <summary>
    /// The unpaged form of <see cref="BuildAudiobookChaptersQuery"/> (JF-784 leg 3):
    /// the concat endpoint (VideoAudioController) passes <c>jellyfinUser: null</c>
    /// (that HTTP path is token-gated with no session user; its library scope arrives
    /// on the token, JF-767), the same unpaged/user-less pattern
    /// <see cref="BuildAlbumTracksQueryUnpaged"/> set for the album arm (JF-763).
    /// Enumerates the whole book in one GetItemList call (paging stays null:
    /// fetch-all; null is the SDK's no-paging value, NOT 0, which is Take(0) per
    /// JF-443). The row-set rationale and the field set live on the ONE core.
    /// </summary>
    /// <param name="jellyfinUser">The query user (session scope), or null on the
    /// token-gated endpoint path.</param>
    /// <param name="bookId">The book folder ID (ParentId scope).</param>
    /// <returns>The unpaged query.</returns>
    internal static InternalItemsQuery BuildAudiobookChaptersQueryUnpaged(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid bookId)
        => BuildAudiobookChaptersQueryCore(jellyfinUser, bookId, startIndex: null, limit: null);

    /// <summary>
    /// The ONE field-set owner both chapters entry points share (paged and
    /// unpaged); a field added here reaches every consumer of the shape.
    /// </summary>
    private static InternalItemsQuery BuildAudiobookChaptersQueryCore(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid bookId,
        int? startIndex,
        int? limit)
        => new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = bookId,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            // Nullable in the SDK: null = no paging (the unpaged form), 0 = Take(0)
            // (JF-443), so the int? params pass through untouched.
            StartIndex = startIndex,
            Limit = limit
        };

    /// <summary>
    /// The scoped sibling of <see cref="BuildAudiobookChaptersQuery"/> (JF-767 Finding A):
    /// builds the chapters query then applies <c>Util.LibraryFilter.ApplyLibraryFilter</c>
    /// in the same call, the same structural pairing
    /// <see cref="BuildScopedAlbumTracksQuery"/> gives the album shape (one definition of
    /// the JF-666 pairing instead of a hand-repeated two-line convention per site). The
    /// executors stay at the callers.
    /// </summary>
    internal static InternalItemsQuery BuildScopedAudiobookChaptersQuery(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        AlexaSkill.Entities.User? pluginUser,
        ILibraryManager libraryManager,
        ILogger logger,
        Guid bookId,
        int startIndex,
        int limit)
    {
        InternalItemsQuery query = BuildAudiobookChaptersQuery(jellyfinUser, bookId, startIndex, limit);
        Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger);
        return query;
    }

    private static IReadOnlyList<BaseItem> FetchAudiobookChapters(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User? pluginUser,
        ILogger logger)
    {
        // Shared builder with PlayBookIntentHandler's initial page (JF-670): one query
        // shape owns head and tail, see the builder's doc. The scoped sibling carries
        // the JF-666 pairing structurally (JF-767): the book folder itself was resolved
        // under the same filter at PlayBook time, so its chapters cannot be scoped out;
        // the parity matters for restricted-library accounts, not for this folder's row
        // membership.
        InternalItemsQuery query = BuildScopedAudiobookChaptersQuery(
            jellyfinUser,
            pluginUser,
            libraryManager,
            logger,
            continuation.ParentId ?? Guid.Empty,
            continuation.StartIndex,
            continuation.BatchSize);

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
