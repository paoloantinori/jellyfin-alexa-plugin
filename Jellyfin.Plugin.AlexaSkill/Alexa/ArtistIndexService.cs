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
    protected override bool ShouldRefreshOn(ItemChangeEventArgs eventArgs) => eventArgs.Item is MusicArtist;

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

        if (topParentIds == null || topParentIds.Length == 0)
        {
            return snapshot.Artists;
        }

        var map = snapshot.TopParentMap;
        var allowed = topParentIds.ToHashSet();
        return snapshot.Artists.Where(a => map.TryGetValue(a.Id, out var parentId) && allowed.Contains(parentId)).ToList();
    }

    /// <inheritdoc />
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> artists = await QueryAllItemsAsync(BaseItemKind.MusicArtist, cancellationToken).ConfigureAwait(false);

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
        var byName = selfMappedArtists
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToLookup(a => a.Name!, a => a.Id, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<BaseItem> albums = await QueryAllItemsAsync(BaseItemKind.MusicAlbum, cancellationToken).ConfigureAwait(false);

        int joinable = byName.Sum(group => group.Count());
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
                parentMemo[musicAlbum.ParentId] = albumTopParent;
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
