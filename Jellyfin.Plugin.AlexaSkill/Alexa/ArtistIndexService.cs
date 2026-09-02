using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Background service that maintains an in-memory index of all MusicArtist items.
/// Loads at startup and refreshes when artists or albums are added/removed from the
/// library. Uses debounced refresh (5s window) to coalesce rapid library changes.
/// </summary>
public class ArtistIndexService : IArtistIndex, IHostedService, IDisposable
{
    private const int RefreshDebounceSeconds = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtistIndexService> _logger;
    private volatile List<BaseItem> _artists = [];
    private volatile Dictionary<Guid, Guid> _artistTopParentMap = new();
    private volatile Dictionary<Guid, (string Primary, string? Alternate)> _phoneticCodes = new();
    private volatile bool _isReady;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _debounceLock = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public bool IsReady => _isReady;
    public int Count => _artists.Count;

    /// <inheritdoc />
    public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
    {
        return _phoneticCodes.TryGetValue(artistId, out codes);
    }

    public ArtistIndexService(ILibraryManager libraryManager, ILogger<ArtistIndexService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null)
    {
        var artists = _artists;

        if (topParentIds == null || topParentIds.Length == 0)
        {
            return artists;
        }

        var map = _artistTopParentMap;
        var allowed = topParentIds.ToHashSet();
        return artists.Where(a => map.TryGetValue(a.Id, out var parentId) && allowed.Contains(parentId)).ToList();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            };

            IReadOnlyList<BaseItem> artists = await Task.Run(
                () => _libraryManager.GetItemList(query), cancellationToken).ConfigureAwait(false);

            // Pre-compute top parent IDs for library filtering
            var topParentMap = new Dictionary<Guid, Guid>(artists.Count);
            var selfMappedArtists = new List<BaseItem>();
            foreach (var artist in artists)
            {
                var topParent = ResolveTopParentId(artist);
                topParentMap[artist.Id] = topParent;
                if (topParent == artist.Id)
                {
                    // Folderless artist (metadata-path MusicArtist, no parent folder):
                    // the walk fell back to the artist's own ID, which never equals a
                    // library folder ID in a filter. Candidate for the album join below.
                    selfMappedArtists.Add(artist);
                }
            }

            // Self-mapped artists inherit their library from their albums; the join
            // skips the album query entirely when nothing is joinable (no self-mapped
            // artists, or all of them blank-named).
            int albumScopedCount = await JoinAlbumLibraryScopeAsync(topParentMap, selfMappedArtists, cancellationToken).ConfigureAwait(false);

            // Pre-compute Double Metaphone phonetic codes for fuzzy matching
            var phoneticCodes = new Dictionary<Guid, (string Primary, string? Alternate)>(artists.Count);
            foreach (var artist in artists)
            {
                if (!string.IsNullOrWhiteSpace(artist.Name))
                {
                    phoneticCodes[artist.Id] = DoubleMetaphone.Encode(artist.Name);
                }
            }

            _artistTopParentMap = topParentMap;
            _phoneticCodes = phoneticCodes;
            _artists = new List<BaseItem>(artists);
            _isReady = true;

            _logger.LogInformation("Artist index {Action}: {Count} artists, {PhoneticCount} with phonetic codes, {AlbumScopedCount} album-scoped",
                artists.Count > 0 ? "loaded" : "initialized (empty library)",
                artists.Count,
                phoneticCodes.Count,
                albumScopedCount);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load artist index");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Fills the library scope of folderless artists from their albums: Jellyfin scopes
    /// such artists to their library through the albums they appear on (the DB stores the
    /// album's folder as the artist's TopParentId), so the index joins album artist names
    /// to self-mapped artists and lets them inherit the album's resolved top parent
    /// (JF-455). One bounded album query per load, no per-artist queries. Jellyfin 10.11
    /// items expose artist NAMES (AlbumArtists/Artists), not artist IDs
    /// (<c>item.ArtistIds</c> does not exist there), so the join is by name; first album
    /// wins, and a folder-derived entry is never overwritten (the self-map guard makes
    /// the write one-shot per artist).
    /// </summary>
    /// <param name="topParentMap">The map being built (mutated in place, pre-publish).</param>
    /// <param name="selfMappedArtists">Artists whose walk returned their own ID.</param>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>The number of artists that inherited a library from an album.</returns>
    private async Task<int> JoinAlbumLibraryScopeAsync(
        Dictionary<Guid, Guid> topParentMap,
        List<BaseItem> selfMappedArtists,
        CancellationToken cancellationToken)
    {
        var byName = selfMappedArtists
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToLookup(a => a.Name!, a => a.Id, StringComparer.OrdinalIgnoreCase);

        // All self-mapped artists have blank names: nothing joinable, skip the
        // full-catalog album query entirely.
        if (byName.Count == 0)
        {
            return 0;
        }

        var albumQuery = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };

        int joinable = byName.Sum(group => group.Count());
        int scoped = 0;

        try
        {
            IReadOnlyList<BaseItem> albums = await Task.Run(
                () => _libraryManager.GetItemList(albumQuery), cancellationToken).ConfigureAwait(false);

            void ScopeArtists(string? artistName, Guid albumTopParent)
            {
                if (string.IsNullOrWhiteSpace(artistName))
                {
                    return;
                }

                foreach (var artistId in byName[artistName!])
                {
                    if (topParentMap[artistId] == artistId)
                    {
                        topParentMap[artistId] = albumTopParent;
                        scoped++;
                    }
                }
            }

            // Siblings share the parent chain, so one walk per distinct parent folder
            // resolves every album under it (full-catalog album sets collapse to a
            // handful of walks). Parentless albums are skipped: they resolve to their
            // own ID, which never equals a library folder ID, so there is no scope to
            // inherit (and their shared Guid.Empty memo key would collide).
            var parentMemo = new Dictionary<Guid, Guid>();
            foreach (var album in albums)
            {
                if (scoped == joinable)
                {
                    break; // every joinable artist already inherited a scope
                }

                if (album is not MusicAlbum musicAlbum || musicAlbum.ParentId == Guid.Empty)
                {
                    continue;
                }

                if (!parentMemo.TryGetValue(musicAlbum.ParentId, out var albumTopParent))
                {
                    albumTopParent = ResolveTopParentId(musicAlbum);

                    // Cache only REAL scopes. A walk that resolved to this album's own
                    // id is a stale parent: caching that value would hand a SIBLING
                    // album under the same dead parent this album's id as its "top
                    // parent"; the sibling would pass the stale check below (the cached
                    // id is not the sibling's own id), consume the one-shot guard, and
                    // scope the artist to an album id in no library's id space. Left
                    // uncached, every stale sibling re-walks one hop and self-detects.
                    if (albumTopParent != musicAlbum.Id)
                    {
                        parentMemo[musicAlbum.ParentId] = albumTopParent;
                    }
                }

                // An album that resolves to its OWN id carries no library scope (stale
                // parent id that no longer resolves). Skip it WITHOUT consuming the
                // one-shot guard, so a later healthy album can still scope the artist.
                if (albumTopParent == musicAlbum.Id)
                {
                    continue;
                }

                foreach (var artistName in musicAlbum.AlbumArtists)
                {
                    ScopeArtists(artistName, albumTopParent);
                }

                foreach (var artistName in musicAlbum.Artists)
                {
                    ScopeArtists(artistName, albumTopParent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown: let the load's own cancellation handling see it
        }
        catch (Exception ex)
        {
            // The join is enrichment, not the load: at this tag a failed load has no
            // background retry, so letting a transient album-query failure propagate
            // would keep the WHOLE artist index cold (every search on the slower DB
            // tiers) until an unrelated library event triggers a refresh. Degrade to
            // the pre-fix scopes: folderless artists keep their self-mapped entries.
            _logger.LogError(ex, "Album library-scope join failed; folderless artists keep their self-mapped scope");
        }

        return scoped;
    }

    /// <summary>
    /// Resolves the library folder ID for an item by walking up the parent chain.
    /// Used for per-user library filtering without DB queries. The stop condition has
    /// FULL parity with Jellyfin's own <c>BaseItem.IsTopParent</c> boundary (all three
    /// edges: plugin folders and channels, live-tv views, and a parent that is the
    /// server-wide <see cref="AggregateFolder"/> root, whose ID cannot discriminate per
    /// library). This is the id space Jellyfin stores as the TopParentId column and the
    /// library filter resolves to, so the index maps and the filter agree for
    /// library-restricted users (JF-455). When the chain ends without a boundary node
    /// the LAST reached node's ID is returned: the top folder's ID for a chain that
    /// tops out at a parentless folder, or the item's own ID when it has no chain at
    /// all (folderless artists stay self-mapped, the album join's signal). Kept
    /// identical to SongNgramIndexService's copy: the shared base class that
    /// single-sources this walk on main does not exist at this tag.
    /// </summary>
    /// <param name="item">The item to resolve.</param>
    /// <returns>The library folder ID (the last reached node's ID when no boundary is hit).</returns>
    private Guid ResolveTopParentId(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        BaseItem? current = item;
        while (current != null)
        {
            BaseItem? parent = current.ParentId == Guid.Empty
                ? null
                : _libraryManager.GetItemById(current.ParentId);

            // IsTopParent parity (BaseItem.cs, v10.11.x): the node itself is a
            // boundary, or its parent is the server-wide aggregate root.
            if (current is BasePluginFolder
                || current is Channel
                || (current is IHasCollectionType view && view.CollectionType == CollectionType.livetv)
                || parent is AggregateFolder)
            {
                return current.Id;
            }

            if (parent == null || !seen.Add(current.Id))
            {
                break; // Chain end or cycle protection
            }

            current = parent;
        }

        // Chain ended without a boundary node (parentless top folder, stale parent
        // id, or cycle): return the LAST REACHED node's id. For a chain that
        // naturally tops out at a parentless folder this is that folder's id (the
        // library root in that shape); for an item with no chain at all (folderless
        // artist) this is the item's own id, the self-map signal the album join
        // keys on.
        return current?.Id ?? item.Id;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        // MusicAlbum too: the library-scope map depends on album data (the folderless-
        // artist join), and an album-only change fires no MusicArtist event (the artist
        // already exists), so without this a new first album would leave the artist
        // unfindable for restricted users until a restart.
        if (_disposed || e.Item is not (MusicArtist or MusicAlbum))
        {
            return;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Debounced artist index refresh failed");
                    }
                },
                null,
                TimeSpan.FromSeconds(RefreshDebounceSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _refreshLock.Dispose();
        _disposed = true;
    }
}
