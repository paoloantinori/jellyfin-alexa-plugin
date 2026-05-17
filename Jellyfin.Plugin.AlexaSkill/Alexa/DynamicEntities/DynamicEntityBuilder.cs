#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// Builds dynamic entity payloads from a user's Jellyfin library.
/// Artists are sourced from the in-memory <see cref="IArtistIndex"/> when available,
/// falling back to database queries. Albums are always queried from the database.
/// Injected into the Alexa NLU session via <c>Dialog.UpdateDynamicEntities</c>.
/// </summary>
public class DynamicEntityBuilder
{
    private const int MaxTotalValueCount = 90;
    private const int DbQueryLimit = 55;
    private const int ArtistIndexLimit = 70;

    private static readonly Dictionary<CatalogType, string> SlotTypeNames = CatalogSlotTypes.Names;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ILogger<DynamicEntityBuilder> _logger;

    public DynamicEntityBuilder(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<DynamicEntityBuilder> logger,
        IArtistIndex? artistIndex = null)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
        _artistIndex = artistIndex;
    }

    /// <summary>
    /// Builds a <c>Dialog.UpdateDynamicEntities</c> directive from the user's library.
    /// Uses the in-memory artist index when available for broader coverage.
    /// </summary>
    public virtual DynamicEntitiesDirective? Build(
        Guid jellyfinUserId,
        string locale,
        Guid[]? allowedLibraryIds,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(jellyfinUserId);
        if (user == null)
        {
            _logger.LogDebug("User {UserId} not found for dynamic entities", jellyfinUserId);
            return null;
        }

        int budget = MaxTotalValueCount;
        Guid[]? topParentIds = allowedLibraryIds != null
            ? LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager)
            : null;

        List<DynamicSlotValue> artistValues;
        if (_artistIndex?.IsReady == true)
        {
            artistValues = BuildArtistValuesFromIndex(topParentIds, locale, ref budget);
        }
        else
        {
            artistValues = BuildSlotValues(user, BaseItemKind.MusicArtist, CatalogType.Artist, locale, topParentIds, ref budget);
        }

        var albumValues = BuildSlotValues(user, BaseItemKind.MusicAlbum, CatalogType.Album, locale, topParentIds, ref budget);

        if (artistValues.Count == 0 && albumValues.Count == 0)
        {
            _logger.LogDebug("No items found for dynamic entities");
            return null;
        }

        var directive = new DynamicEntitiesDirective();

        if (artistValues.Count > 0)
        {
            directive.Types.Add(new DynamicSlotType
            {
                Name = SlotTypeNames[CatalogType.Artist],
                Values = artistValues
            });
        }

        if (albumValues.Count > 0)
        {
            directive.Types.Add(new DynamicSlotType
            {
                Name = SlotTypeNames[CatalogType.Album],
                Values = albumValues
            });
        }

        _logger.LogDebug(
            "Built dynamic entities: {Artists} artists ({ArtistSource}) and {Albums} albums ({Used} of {Max} budget)",
            artistValues.Count,
            _artistIndex?.IsReady == true ? "index" : "db",
            albumValues.Count,
            MaxTotalValueCount - budget,
            MaxTotalValueCount);

        return directive;
    }

    private List<DynamicSlotValue> BuildArtistValuesFromIndex(
        Guid[]? topParentIds,
        string locale,
        ref int budget)
    {
        var allArtists = _artistIndex!.GetArtists(topParentIds);
        var values = new List<DynamicSlotValue>();

        foreach (BaseItem item in allArtists)
        {
            if (values.Count >= ArtistIndexLimit || !TryAddSlotValue(values, item, CatalogType.Artist, locale, ref budget))
            {
                break;
            }
        }

        return values;
    }

    private List<DynamicSlotValue> BuildSlotValues(
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind itemKind,
        CatalogType catalogType,
        string locale,
        Guid[]? topParentIds,
        ref int budget)
    {
        var query = new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true),
            Limit = DbQueryLimit,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        if (topParentIds != null)
        {
            query.TopParentIds = topParentIds;
        }

        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);

        var values = new List<DynamicSlotValue>();

        foreach (BaseItem item in items)
        {
            if (!TryAddSlotValue(values, item, catalogType, locale, ref budget))
            {
                break;
            }
        }

        return values;
    }

    private bool TryAddSlotValue(
        List<DynamicSlotValue> values,
        BaseItem item,
        CatalogType catalogType,
        string locale,
        ref int budget)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || budget <= 0)
        {
            return budget > 0;
        }

        var synonyms = PhoneticSynonymGenerator.GenerateSynonyms(item.Name, locale);
        int cost = 1 + synonyms.Count;

        if (cost > budget)
        {
            int fitSynonyms = Math.Max(0, budget - 1);
            synonyms = synonyms.Take(fitSynonyms).ToList();
            cost = 1 + synonyms.Count;
        }

        if (cost > budget)
        {
            return false;
        }

        values.Add(new DynamicSlotValue
        {
            Id = CatalogValue.FormatId(catalogType, item.Id),
            Name = new DynamicSlotValueName
            {
                Value = item.Name,
                Synonyms = synonyms.Count > 0 ? synonyms : null
            }
        });

        budget -= cost;
        return true;
    }
}
