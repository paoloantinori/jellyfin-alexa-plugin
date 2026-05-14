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
/// Builds dynamic entity payloads from a user's recently added Jellyfin items.
/// These are injected into the Alexa NLU session for improved recognition
/// of artists and albums the user is likely to request.
/// </summary>
public class DynamicEntityBuilder
{
    /// <summary>
    /// Alexa allows at most 100 total values+synonyms across all slot types in a
    /// single Dialog.UpdateDynamicEntities directive. We reserve headroom so
    /// artists + albums together stay under this limit.
    /// </summary>
    private const int MaxTotalValueCount = 90;
    private const int QueryLimit = 55;

    private static readonly Dictionary<CatalogType, string> SlotTypeNames = CatalogSlotTypes.Names;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<DynamicEntityBuilder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicEntityBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <param name="logger">Logger instance.</param>
    public DynamicEntityBuilder(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<DynamicEntityBuilder> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Builds a Dialog.UpdateDynamicEntities directive from the user's recently added items.
    /// Returns null if no items are found.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID to query recent items for.</param>
    /// <param name="locale">The Alexa request locale for phonetic synonym generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated directive, or null if no items available.</returns>
    public virtual DynamicEntitiesDirective? BuildFromRecentItems(
        Guid jellyfinUserId,
        string locale,
        CancellationToken cancellationToken)
    {
        return BuildFromRecentItems(jellyfinUserId, locale, null, cancellationToken);
    }

    /// <summary>
    /// Builds a Dialog.UpdateDynamicEntities directive from the user's recently added items,
    /// optionally filtered by allowed library IDs.
    /// Returns null if no items are found.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID to query recent items for.</param>
    /// <param name="locale">The Alexa request locale for phonetic synonym generation.</param>
    /// <param name="allowedLibraryIds">Optional library GUIDs to restrict results to. Null = all libraries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated directive, or null if no items available.</returns>
    public virtual DynamicEntitiesDirective? BuildFromRecentItems(
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
        var artistValues = BuildSlotValues(user, BaseItemKind.MusicArtist, CatalogType.Artist, locale, topParentIds, ref budget);
        var albumValues = BuildSlotValues(user, BaseItemKind.MusicAlbum, CatalogType.Album, locale, topParentIds, ref budget);

        if (artistValues.Count == 0 && albumValues.Count == 0)
        {
            _logger.LogDebug("No recent items found for dynamic entities");
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
            "Built dynamic entities directive with {Artists} artists and {Albums} albums ({Total} total values+synonyms)",
            artistValues.Count, albumValues.Count, MaxTotalValueCount - budget);


        return directive;
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
            Limit = QueryLimit,
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
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            if (budget <= 0)
            {
                break;
            }

            var synonyms = PhoneticSynonymGenerator.GenerateSynonyms(item.Name, locale);

            // Each value counts as 1, plus 1 per synonym toward the Alexa limit.
            int cost = 1 + synonyms.Count;
            if (cost > budget)
            {
                // Try trimming synonyms to fit.
                int fitSynonyms = Math.Max(0, budget - 1);
                if (fitSynonyms == 0 && budget >= 1)
                {
                    synonyms = new List<string>();
                    cost = 1;
                }
                else
                {
                    synonyms = synonyms.Take(fitSynonyms).ToList();
                    cost = 1 + synonyms.Count;
                }
            }

            if (cost > budget)
            {
                break;
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
        }

        return values;
    }
}
