using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Background service that maintains an in-memory index of all MusicArtist items.
/// Loads at startup and refreshes when artists are added/removed from the library.
/// Lifecycle (debounce, failed-load retry, readiness, dispose ordering) lives in
/// <see cref="DebouncedLibraryIndexService"/> (JF-419.3 extraction). The loaded state is
/// published as one immutable <see cref="ArtistIndexSnapshot"/> reference so a reader can
/// never observe a torn mix of two loads (JF-432).
/// </summary>
public class ArtistIndexService : DebouncedLibraryIndexService, IArtistIndex
{
    private volatile ArtistIndexSnapshot _snapshot = ArtistIndexSnapshot.Empty;

    /// <summary>The currently published snapshot (internal test accessor, JF-432).</summary>
    internal ArtistIndexSnapshot CurrentSnapshot => _snapshot;

    /// <inheritdoc />
    public int Count => _snapshot.Artists.Count;

    /// <inheritdoc />
    protected override string IndexName => "artist";

    /// <inheritdoc />
    // MusicAlbum too: the library-scope map depends on album data (the folderless-
    // artist join), and an album-only change fires no MusicArtist event (the artist
    // already exists), so without this a new first album would leave the artist
    // unfindable for restricted users until a restart (code-review F3, JF-455).
    protected override bool ShouldRefreshOn(ItemChangeEventArgs eventArgs) => eventArgs.Item is MusicArtist or MusicAlbum;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistIndexService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager for the load query and change events.</param>
    /// <param name="logger">Logger instance.</param>
    public ArtistIndexService(ILibraryManager libraryManager, ILogger<ArtistIndexService> logger)
        : base(libraryManager, logger)
    {
    }

    /// <inheritdoc />
    public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
    {
        return _snapshot.PhoneticCodes.TryGetValue(artistId, out codes);
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null)
    {
        // Capture once: the artist list and the parent map must come from the same publish
        var snapshot = _snapshot;
        return FilterByLibraryScope(snapshot.Artists, a => a.Id, snapshot.TopParentMap, topParentIds);
    }

    /// <inheritdoc />
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> artists = await QueryAllItemsAsync(BaseItemKind.MusicArtist, cancellationToken).ConfigureAwait(false);

        // Pre-compute top parent IDs for library filtering via the shared per-load
        // chain memo (rationale on ResolveTopParentIdMemoized).
        var topParentMap = new Dictionary<Guid, Guid>(artists.Count);
        var selfMappedArtists = new List<BaseItem>();
        var chainMemo = new Dictionary<Guid, Guid>();
        foreach (var artist in artists)
        {
            Guid topParent = ResolveTopParentIdMemoized(artist, chainMemo);
            topParentMap[artist.Id] = topParent;
            if (topParent == artist.Id)
            {
                // Folderless artist (metadata-path MusicArtist, no parent folder):
                // the walk fell back to the artist's own ID, which never equals a
                // library folder ID in a filter. Candidate for the album join below.
                selfMappedArtists.Add(artist);
            }
        }

        int albumScopedCount = 0;
        if (selfMappedArtists.Count > 0)
        {
            albumScopedCount = await JoinAlbumLibraryScopeAsync(topParentMap, selfMappedArtists, cancellationToken).ConfigureAwait(false);
        }

        // Pre-compute Double Metaphone phonetic codes for fuzzy matching
        var phoneticCodes = new Dictionary<Guid, (string Primary, string? Alternate)>(artists.Count);
        foreach (var artist in artists)
        {
            if (!string.IsNullOrWhiteSpace(artist.Name))
            {
                phoneticCodes[artist.Id] = DoubleMetaphone.Encode(artist.Name);
            }
        }

        // One publish: all read paths see the new maps and the new list together (JF-432)
        _snapshot = new ArtistIndexSnapshot(new List<BaseItem>(artists), topParentMap, phoneticCodes);

        Logger.LogInformation("Artist index {Action}: {Count} artists, {PhoneticCount} with phonetic codes, {AlbumScopedCount} album-scoped",
            artists.Count > 0 ? "loaded" : "initialized (empty library)",
            artists.Count,
            phoneticCodes.Count,
            albumScopedCount);
    }

    /// <summary>
    /// Fills the library scope of folderless artists from their albums: Jellyfin scopes
    /// such artists to their library through the albums they appear on (the DB stores the
    /// album's folder as the artist's TopParentId), so the index joins album artist names
    /// to self-mapped artists and lets them inherit the album's resolved top parent
    /// (JF-455). One bounded album query per load, no per-artist queries. Jellyfin items
    /// expose artist NAMES (AlbumArtists/Artists), not artist IDs, so the join is by
    /// name; first album wins, and a folder-derived entry is never overwritten (the
    /// self-map guard makes the write one-shot per artist).
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
        // Materialize the named list once (JF-456): it is both the empty-check and the
        // joinable count, so the count can never drift from the lookup's Where filter
        // it used to be re-derived from.
        var namedArtists = selfMappedArtists
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToList();

        // All self-mapped artists have blank names: nothing joinable, skip the
        // full-catalog album query entirely (code-review F10, JF-455).
        if (namedArtists.Count == 0)
        {
            return 0;
        }

        var byName = namedArtists
            .ToLookup(a => a.Name!, a => a.Id, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<BaseItem> albums = await QueryAllItemsAsync(BaseItemKind.MusicAlbum, cancellationToken).ConfigureAwait(false);

        int joinable = namedArtists.Count;
        int scoped = 0;

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
                // uncached, every stale sibling re-walks one hop and self-detects
                // (0.12.1 port review, backported).
                if (albumTopParent != musicAlbum.Id)
                {
                    parentMemo[musicAlbum.ParentId] = albumTopParent;
                }
            }

            // An album that resolves to its OWN id carries no library scope (stale
            // parent id that no longer resolves). Skip it WITHOUT consuming the
            // one-shot guard, so a later healthy album can still scope the artist
            // (code-review F4, JF-455).
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

        return scoped;
    }
}
