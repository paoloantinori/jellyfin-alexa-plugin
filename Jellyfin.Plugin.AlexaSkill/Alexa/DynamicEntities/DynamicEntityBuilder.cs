#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
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
    // Alexa limits Dialog.UpdateDynamicEntities to 100 total values + synonyms across all slot types.
    private const int MaxTotalSlots = 100;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated directive, or null if no items available.</returns>
    public virtual DynamicEntitiesDirective? BuildFromRecentItems(
        Guid jellyfinUserId,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(jellyfinUserId);
        if (user == null)
        {
            _logger.LogDebug("User {UserId} not found for dynamic entities", jellyfinUserId);
            return null;
        }

        int budget = MaxTotalSlots;
        var artistValues = BuildSlotValues(user, BaseItemKind.MusicArtist, CatalogType.Artist, ref budget);
        var albumValues = BuildSlotValues(user, BaseItemKind.MusicAlbum, CatalogType.Album, ref budget);

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
            "Built dynamic entities directive with {Artists} artists and {Albums} albums ({Remaining} budget remaining)",
            artistValues.Count, albumValues.Count, budget);

        return directive;
    }

    private List<DynamicSlotValue> BuildSlotValues(
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind itemKind,
        CatalogType catalogType,
        ref int budget)
    {
        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true),
            Limit = QueryLimit,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        });

        var values = new List<DynamicSlotValue>();

        foreach (BaseItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            // Each value costs 1 + synonym count toward the Alexa platform limit.
            var synonyms = PhoneticSynonymGenerator.GenerateSynonyms(item.Name);
            int cost = 1 + synonyms.Count;

            if (budget < cost)
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
