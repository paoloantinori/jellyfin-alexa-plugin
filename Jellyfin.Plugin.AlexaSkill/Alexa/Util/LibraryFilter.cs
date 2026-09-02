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
    /// Whether an item kind lives OUTSIDE every media library: its items carry a top
    /// parent (the PlaylistsFolder, the livetv view) that no per-user library
    /// restriction can ever contain, so a <see cref="InternalItemsQuery.TopParentIds"/>
    /// filter excludes the kind entirely (JF-455/GH #22). This is the single decision
    /// point for the exemption: every query site calls
    /// <see cref="ApplyLibraryFilter(InternalItemsQuery, Entities.User?, ILibraryManager, ILogger?, bool)"/>
    /// and lets this predicate decide, instead of each handler knowing the list.
    /// </summary>
    /// <param name="kind">The item kind.</param>
    /// <returns>True when the kind is exempt from library filtering.</returns>
    public static bool IsOutOfLibraryKind(BaseItemKind kind)
        => kind is BaseItemKind.Playlist or BaseItemKind.LiveTvChannel;

    /// <summary>
    /// Whether every included kind of a query is out-of-library, in which case the
    /// TopParentIds filter must be skipped (it could only return zero rows). Mixed
    /// kind sets keep the filter: the in-library rows are the majority and the
    /// out-of-library kinds need their own unfiltered query (see SearchMedia, JF-456).
    /// A null/empty kind array means "all kinds" to Jellyfin and keeps the filter.
    /// </summary>
    /// <param name="includeItemTypes">The query's IncludeItemTypes array.</param>
    /// <returns>True when the whole query is library-exempt.</returns>
    internal static bool IsEntirelyOutOfLibrary(BaseItemKind[]? includeItemTypes)
        => includeItemTypes is { Length: > 0 } && Array.TrueForAll(includeItemTypes, IsOutOfLibraryKind);

    /// <summary>
    /// The AUTOMATIC items-by-name bypass, shared by both ApplyLibraryFilter
    /// overloads: the single decision point for
    /// artist queries, so no call site can miss it (the per-site wiring had missed
    /// BrowseLibrary's parametric IncludeItemTypes=[itemType] shape, code-review
    /// round 2 item 1). Sets <see cref="InternalItemsQuery.IncludeItemsByName"/>
    /// when the TopParentIds filter was just applied to a query that includes
    /// <see cref="BaseItemKind.MusicArtist"/>.
    /// <para>
    /// Why: IncludeItemsByName activates Jellyfin's items-by-name bypass for the
    /// TopParentIds filter (BaseItemRepository: "type in itemByNameTypes ||
    /// TopParentId in ids" when the flag is set and MusicArtist is an included
    /// type). Jellyfin stores TopParentId NULL for folderless artists (the
    /// metadata-path majority, 1063/1149 verified live), so WITHOUT the flag any
    /// library-restricted DB artist query matches zero rows in the post-restart
    /// cold-index window. Inert without TopParentIds: Jellyfin evaluates the bypass
    /// only inside its TopParentIds branch, so unrestricted users are unaffected.
    /// Bounded trade-off: the bypass matches ALL MusicArtist rows regardless of
    /// library, so a wrong-library artist can be found; its songs query still
    /// filters by TopParentIds and the play degrades to artist-found-no-songs (the
    /// JF-455 F1 shape), never a content leak.
    /// </para>
    /// The steady-state catalog surfaces (LibrarySyncService.FetchLibraryItems,
    /// DynamicEntityBuilder.BuildSlotValues) opt OUT via
    /// <c>includeItemsByName: false</c>: there the bypass would persist
    /// excluded-library artist names in the SMAPI catalog upload and the
    /// per-session dynamic entity values instead of surfacing them transiently in
    /// a cold-window search (JF-457).
    /// </summary>
    /// <param name="query">The query just scoped with TopParentIds.</param>
    /// <param name="includeItemsByName">False only at the opt-out catalog surfaces.</param>
    private static void ApplyItemsByNameBypass(InternalItemsQuery query, bool includeItemsByName)
    {
        // Gated on the filter actually being applied (Jellyfin evaluates the bypass
        // only in its TopParentIds branch) and on an EXPLICIT MusicArtist entry:
        // an empty IncludeItemTypes means "all kinds" to Jellyfin, and silently
        // widening a kind-less query to every artist row must stay a deliberate
        // choice at the call site.
        if (includeItemsByName && query.TopParentIds.Length > 0 && Array.Exists(query.IncludeItemTypes, k => k == BaseItemKind.MusicArtist))
        {
            query.IncludeItemsByName = true;
        }
    }

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
    /// Resolves CollectionFolder IDs to their physical top-level folder IDs and returns
    /// the UNION with the original CollectionFolder IDs (deduplicated). The plugin's
    /// in-memory index maps are keyed by physical folder IDs (the parent walk stops at
    /// the AggregateFolder boundary), and Jellyfin's database accepts both id spaces
    /// for <see cref="InternalItemsQuery.TopParentIds"/> (verified on 10.11.11, where
    /// no row carries a CollectionFolder id as TopParentId, so the CF half is inert
    /// there and kept only as a defensive fallback for servers with other layouts).
    /// Results are cached until <see cref="InvalidateCache"/> is called.
    /// </summary>
    /// <param name="collectionFolderIds">CollectionFolder GUIDs from plugin config.</param>
    /// <param name="libraryManager">Jellyfin library manager for resolving folders.</param>
    /// <param name="logger">Optional logger for cache hit/miss diagnostics.</param>
    /// <returns>Physical folder GUIDs unioned with the original CollectionFolder GUIDs.</returns>
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
        // Collect only the resolved physical folder IDs: the union with the inputs
        // below re-adds every configured id (CollectionFolder fallback included), so
        // unresolved inputs need no per-id fallback branch.
        var resolved = new List<Guid>();
        foreach (var id in collectionFolderIds)
        {
            if (libraryManager.GetItemById(id) is CollectionFolder cf)
            {
                // CollectionFolder stores physical paths (e.g. /data/media/video/cartoni)
                // in PhysicalLocationsList. We resolve each path to its Folder item to
                // get the physical folder ID that items actually use as TopParentId.
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
            }
        }

        sw.Stop();
        Guid[] result = resolved.Concat(collectionFolderIds).Distinct().ToArray();
        _topParentCache[cacheKey] = result;
        logger?.LogDebug("LibraryFilter: cache miss, resolved {InputCount} → {OutputCount} top parents in {Ms}ms", collectionFolderIds.Length, result.Length, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Fused one-call resolution of a user's library scope: parses
    /// <see cref="GetAllowedLibraryIds"/> and resolves <see cref="ResolveTopParentIds"/>
    /// in a single step, returning null when the user is unrestricted. The value the
    /// in-memory index surfaces (and anything derived from them) must consume, so raw
    /// CollectionFolder ids never escape the reconciliation point (JF-456).
    /// </summary>
    /// <param name="user">The plugin user entity, or null.</param>
    /// <param name="libraryManager">Jellyfin library manager for resolving folders.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Resolved top-parent ids (physical unioned with CollectionFolder), or null when unrestricted.</returns>
    public static Guid[]? ResolveForUser(Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null)
    {
        var allowedIds = GetAllowedLibraryIds(user);
        return allowedIds != null ? ResolveTopParentIds(allowedIds, libraryManager, logger) : null;
    }

    /// <summary>
    /// Applies per-user library filtering to a query by setting TopParentIds.
    /// Resolves CollectionFolder IDs to physical folder IDs for correct filtering.
    /// No-op when the user has no library restrictions configured, and when every
    /// included kind is out-of-library (<see cref="IsOutOfLibraryKind"/>): those items
    /// live under top parents no restriction contains, so the filter could only
    /// exclude them all (JF-456, GH #22 residuals: playlist search, live-TV channels).
    /// When the filter IS applied to a query that includes MusicArtist, the
    /// items-by-name bypass is set automatically (<see cref="ApplyItemsByNameBypass"/>);
    /// pass <c>includeItemsByName: false</c> only at the steady-state catalog
    /// surfaces that must stay strict (see that method's doc).
    /// </summary>
    /// <param name="query">The Jellyfin query to filter.</param>
    /// <param name="user">The plugin user entity, or null.</param>
    /// <param name="libraryManager">Jellyfin library manager for resolving CollectionFolder → physical folder mapping.</param>
    /// <param name="logger">Optional logger for debug diagnostics.</param>
    /// <param name="includeItemsByName">Whether the automatic MusicArtist items-by-name bypass may fire (opt-out for the catalog surfaces).</param>
    public static void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null, bool includeItemsByName = true)
    {
        if (IsEntirelyOutOfLibrary(query.IncludeItemTypes))
        {
            logger?.LogDebug(
                "ApplyLibraryFilter: skipping library filter for user {UserId}, all included kinds are out-of-library ({Kinds})",
                user?.Id,
                string.Join(",", query.IncludeItemTypes));
            return;
        }

        var allowedIds = GetAllowedLibraryIds(user);
        if (allowedIds != null)
        {
            query.TopParentIds = ResolveTopParentIds(allowedIds, libraryManager, logger);
            ApplyItemsByNameBypass(query, includeItemsByName);
            logger?.LogDebug("ApplyLibraryFilter: user={UserId} libraries={Count} resolvedTopParents={TopParentCount}", user?.Id, allowedIds.Length, query.TopParentIds.Length);
        }
        else
        {
            logger?.LogDebug("ApplyLibraryFilter: no library restrictions for user {UserId}", user?.Id);
        }
    }

    /// <summary>
    /// Pre-resolved-scope variant of
    /// <see cref="ApplyLibraryFilter(InternalItemsQuery, Entities.User?, ILibraryManager, ILogger?, bool)"/>:
    /// assigns a scope resolved once via <see cref="ResolveForUser"/> instead of
    /// re-resolving per query (the E4 hoist: one resolution shared by the in-memory
    /// read and every database tier). Semantics mirror the user-resolving overload,
    /// including the <see cref="IsEntirelyOutOfLibrary"/> exemption (an all-exempt
    /// kind set is left unfiltered even with a non-null scope) and the automatic
    /// MusicArtist items-by-name bypass. A null scope (unrestricted user) leaves
    /// the query unfiltered.
    /// </summary>
    /// <param name="query">The Jellyfin query to filter.</param>
    /// <param name="topParentIds">Resolved top-parent scope from <see cref="ResolveForUser"/>, or null when unrestricted.</param>
    /// <param name="includeItemsByName">Whether the automatic MusicArtist items-by-name bypass may fire (opt-out for the catalog surfaces).</param>
    public static void ApplyLibraryFilter(InternalItemsQuery query, Guid[]? topParentIds, bool includeItemsByName = true)
    {
        if (IsEntirelyOutOfLibrary(query.IncludeItemTypes))
        {
            return;
        }

        // Null = unrestricted: leave TopParentIds at its framework default (the
        // empty array) rather than writing null, matching the user-resolving
        // overload. The bypass is inert without TopParentIds either way.
        if (topParentIds != null)
        {
            query.TopParentIds = topParentIds;
            ApplyItemsByNameBypass(query, includeItemsByName);
        }
    }
}
