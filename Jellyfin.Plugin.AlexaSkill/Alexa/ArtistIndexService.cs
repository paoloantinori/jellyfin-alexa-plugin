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
/// <see cref="DebouncedLibraryIndexService"/> (JF-419.3 extraction).
/// </summary>
public class ArtistIndexService : DebouncedLibraryIndexService, IArtistIndex
{
    private volatile List<BaseItem> _artists = [];
    private volatile Dictionary<Guid, Guid> _artistTopParentMap = new();
    private volatile Dictionary<Guid, (string Primary, string? Alternate)> _phoneticCodes = new();

    /// <inheritdoc />
    public int Count => _artists.Count;

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
        return _phoneticCodes.TryGetValue(artistId, out codes);
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null)
    {
        var artists = _artists;

        if (topParentIds == null || topParentIds.Length == 0)
        {
            return artists;
        }

        var map = _artistTopParentMap;
        return artists.Where(a => map.TryGetValue(a.Id, out var parentId) && Array.IndexOf(topParentIds, parentId) >= 0).ToList();
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

        _artistTopParentMap = topParentMap;
        _phoneticCodes = phoneticCodes;
        _artists = new List<BaseItem>(artists);

        Logger.LogInformation("Artist index {Action}: {Count} artists, {PhoneticCount} with phonetic codes",
            artists.Count > 0 ? "loaded" : "initialized (empty library)",
            artists.Count,
            phoneticCodes.Count);
    }
}
