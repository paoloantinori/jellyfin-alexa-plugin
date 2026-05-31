#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Shared utility for applying per-user library restrictions to Jellyfin queries.
/// Used by intent handlers, catalog sync, and dynamic entity building.
/// </summary>
public static class LibraryFilter
{
    /// <summary>
    /// Cache for resolved top-parent IDs. Invalidated when user config changes
    /// (library restrictions updated), never expires otherwise since folder
    /// structure only changes on admin action.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Guid[]> _topParentCache = new();

    /// <summary>
    /// Invalidate the entire top-parent cache. Call when a user's AllowedLibraryIds changes.
    /// </summary>
    public static void InvalidateCache() => _topParentCache.Clear();

    /// <summary>
    /// Parses the user's AllowedLibraryIds from strings to Guids.
    /// Returns null when no restriction is configured (backward compatible default).
    /// </summary>
    /// <param name="user">The plugin user entity, or null.</param>
    /// <returns>Array of allowed library GUIDs, or null if unrestricted.</returns>
    public static Guid[]? GetAllowedLibraryIds(Entities.User? user)
    {
        if (user?.AllowedLibraryIds == null || user.AllowedLibraryIds.Count == 0)
        {
            return null;
        }

        var ids = new List<Guid>(user.AllowedLibraryIds.Count);
        foreach (var idStr in user.AllowedLibraryIds)
        {
            if (Guid.TryParse(idStr, out var id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids.ToArray() : null;
    }

    /// <summary>
    /// Resolves CollectionFolder IDs to their physical top-level folder IDs.
    /// Jellyfin's <see cref="InternalItemsQuery.TopParentIds"/> expects physical folder
    /// IDs (the actual media folders), not the virtual <see cref="CollectionFolder"/> IDs
    /// that the config UI stores from <c>/Library/MediaFolders</c>.
    /// Results are cached until <see cref="InvalidateCache"/> is called.
    /// </summary>
    /// <param name="collectionFolderIds">CollectionFolder GUIDs from plugin config.</param>
    /// <param name="libraryManager">Jellyfin library manager for resolving folders.</param>
    /// <param name="logger">Optional logger for cache hit/miss diagnostics.</param>
    /// <returns>Physical folder GUIDs suitable for TopParentIds, or the originals if resolution fails.</returns>
    public static Guid[] ResolveTopParentIds(Guid[] collectionFolderIds, ILibraryManager libraryManager, ILogger? logger = null)
    {
        if (collectionFolderIds.Length == 0)
        {
            return collectionFolderIds;
        }

        string cacheKey = string.Join(",", collectionFolderIds.OrderBy(g => g));
        if (_topParentCache.TryGetValue(cacheKey, out Guid[]? cached))
        {
            logger?.LogDebug("LibraryFilter: cache hit for {Count} libraries", collectionFolderIds.Length);
            return cached;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resolved = new List<Guid>();
        foreach (var id in collectionFolderIds)
        {
            var item = libraryManager.GetItemById(id);
            if (item is CollectionFolder cf)
            {
                // CollectionFolder stores physical paths (e.g. /data/media/video/cartoni)
                // in PhysicalLocationsList. We resolve each path to its Folder item to
                // get the physical folder ID that items actually use as TopParentId.
                int before = resolved.Count;
                var locations = cf.PhysicalLocationsList;
                if (locations != null && locations.Any())
                {
                    foreach (var path in locations)
                    {
                        var folder = libraryManager.FindByPath(path, true);
                        if (folder != null)
                        {
                            resolved.Add(folder.Id);
                        }
                    }
                }

                if (resolved.Count == before)
                {
                    resolved.Add(id);
                }
            }
            else
            {
                resolved.Add(id);
            }
        }

        sw.Stop();
        Guid[] result = resolved.Count > 0 ? resolved.ToArray() : collectionFolderIds;
        _topParentCache[cacheKey] = result;
        logger?.LogDebug("LibraryFilter: cache miss, resolved {InputCount} → {OutputCount} top parents in {Ms}ms", collectionFolderIds.Length, result.Length, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Applies per-user library filtering to a query by setting TopParentIds.
    /// Resolves CollectionFolder IDs to physical folder IDs for correct filtering.
    /// No-op when the user has no library restrictions configured.
    /// </summary>
    /// <param name="query">The Jellyfin query to filter.</param>
    /// <param name="user">The plugin user entity, or null.</param>
    /// <param name="libraryManager">Jellyfin library manager for resolving CollectionFolder → physical folder mapping.</param>
    /// <param name="logger">Optional logger for debug diagnostics.</param>
    public static void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null)
    {
        var allowedIds = GetAllowedLibraryIds(user);
        if (allowedIds != null)
        {
            query.TopParentIds = ResolveTopParentIds(allowedIds, libraryManager, logger);
            logger?.LogDebug("ApplyLibraryFilter: user={UserId} libraries={Count} resolvedTopParents={TopParentCount}", user?.Id, allowedIds.Length, query.TopParentIds.Length);
        }
        else
        {
            logger?.LogDebug("ApplyLibraryFilter: no library restrictions for user {UserId}", user?.Id);
        }
    }
}
