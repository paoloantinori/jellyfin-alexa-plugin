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
        return snapshot.Artists.Where(a => map.TryGetValue(a.Id, out var parentId) && Array.IndexOf(topParentIds, parentId) >= 0).ToList();
    }

    /// <inheritdoc />
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> artists = await Task.Run(
            () => LibraryManager.GetItemList(query), cancellationToken).ConfigureAwait(false);

        // Pre-compute top parent IDs for library filtering
        var topParentMap = new Dictionary<Guid, Guid>(artists.Count);
        foreach (var artist in artists)
        {
            topParentMap[artist.Id] = ResolveTopParentId(artist);
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

        Logger.LogInformation("Artist index {Action}: {Count} artists, {PhoneticCount} with phonetic codes",
            artists.Count > 0 ? "loaded" : "initialized (empty library)",
            artists.Count,
            phoneticCodes.Count);
    }
}
