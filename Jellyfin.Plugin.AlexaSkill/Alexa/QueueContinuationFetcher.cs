using System;
using System.Collections.Generic;
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
    /// Fetch the next batch of items based on continuation data.
    /// Updates the continuation's StartIndex after fetching.
    /// </summary>
    /// <param name="continuation">The continuation state.</param>
    /// <param name="libraryManager">The library manager for queries.</param>
    /// <param name="userManager">The user manager for resolving Jellyfin users.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>The fetched items, or empty list if no more items.</returns>
    public static IReadOnlyList<BaseItem> FetchNextBatch(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger logger)
    {
        if (continuation.StartIndex >= continuation.TotalCount)
        {
            return Array.Empty<BaseItem>();
        }

        var jellyfinUser = userManager.GetUserById(continuation.UserId);

        IReadOnlyList<BaseItem> items = continuation.SourceType switch
        {
            "Album" => FetchAlbumTracks(continuation, libraryManager, jellyfinUser),
            "Artist" => FetchArtistSongs(continuation, libraryManager, jellyfinUser),
            "Playlist" => FetchPlaylistItems(continuation, libraryManager, jellyfinUser),
            _ => Array.Empty<BaseItem>()
        };

        if (items.Count > 0)
        {
            logger.LogInformation(
                "Progressive queue: fetched {Count} items for {SourceType} (offset {StartIndex}/{Total})",
                items.Count,
                continuation.SourceType,
                continuation.StartIndex,
                continuation.TotalCount);
        }

        return items;
    }

    private static IReadOnlyList<BaseItem> FetchAlbumTracks(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = continuation.ParentId ?? Guid.Empty,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            StartIndex = continuation.StartIndex,
            Limit = continuation.BatchSize
        };

        QueryResult<BaseItem> result = libraryManager.GetItemsResult(query);
        continuation.StartIndex += result.Items.Count;
        return result.Items;
    }

    private static IReadOnlyList<BaseItem> FetchArtistSongs(
        QueueContinuation continuation,
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            ArtistIds = continuation.ArtistId.HasValue ? new[] { continuation.ArtistId.Value } : Array.Empty<Guid>(),
            StartIndex = continuation.StartIndex,
            Limit = continuation.BatchSize
        };

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
