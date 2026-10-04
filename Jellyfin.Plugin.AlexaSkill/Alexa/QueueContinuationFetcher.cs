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
        // below must name the offset the query ran at (the artist fetcher marks
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

        // Artist continuations carry TotalCount=UnknownTotal (GetItemList has no
        // count); audiobook continuations carry it when the initial page came through
        // the NRE fallback (JF-673). Render the regime honestly instead of a
        // meaningless 2147483647.
        string totalText = continuation.TotalCount == Util.SearchService.UnknownTotal
            ? "end-unknown"
            : continuation.TotalCount.ToString(CultureInfo.InvariantCulture);

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

        QueryResult<BaseItem> result = libraryManager.GetItemsResult(query);

        // Tolerant fallback: for split / multi-disc / malformed-folder albums the
        // folder-based ParentId query returns 0 (PlayAlbumIntentHandler's initial fetch
        // retries by AlbumIds for the same reason — JF-338). Mirror that here or
        // progressive continuation truncates the album to the initial page.
        if (result.TotalRecordCount == 0 && continuation.ParentId.HasValue)
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
            result = libraryManager.GetItemsResult(albumIdsQuery);
        }

        continuation.StartIndex += result.Items.Count;
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

        // JF-673 end-unknown regime: the head stored UnknownTotal when the initial
        // page came through the NRE fallback (GetItemList has no total), so there is
        // no total to exhaust against; the only end signal is a SHORT page, the
        // FetchArtistSongs shape. Marking the continuation exhausted here keeps the
        // NEXT FetchNextBatch terminal via the entry guard instead of querying past
        // the end and WARNing. In the known-total regime the shape is unchanged
        // (advance; the entry guard ends it at StartIndex >= TotalCount).
        // Accepted noise (code-review JF-673): a book whose chapter count is an exact
        // multiple of the page sizes ends on a ZERO-item tail batch, so the
        // dispatcher's zero-page WARN fires once for a normally completed book, the
        // same boundary noise the artist fetcher has always carried.
        if (continuation.TotalCount == Util.SearchService.UnknownTotal && result.Items.Count < continuation.BatchSize)
        {
            continuation.StartIndex = continuation.TotalCount;
        }
        else
        {
            continuation.StartIndex += result.Items.Count;
        }

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

        // Detect end of results: if we got fewer items than requested, update TotalCount
        // so FetchNextBatch knows there are no more items.
        if (items.Count < continuation.BatchSize)
        {
            continuation.StartIndex = continuation.TotalCount;
        }
        else
        {
            continuation.StartIndex += items.Count;
        }

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

            continuation.StartIndex += batch.Count;
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

        continuation.StartIndex += fallback.Count;
        return fallback;
    }
}
