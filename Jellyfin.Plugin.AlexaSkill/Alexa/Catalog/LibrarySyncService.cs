#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Orchestrates syncing Jellyfin library items to SMAPI catalogs
/// for improved Alexa recognition.
/// </summary>
public class LibrarySyncService
{
    private const int MaxCatalogValues = 50000;
    private const string DevelopmentStage = "development";
    private const string ItalianLocale = "it-IT";
    private const string ArtistSlotType = "JELLYFIN_ARTIST";
    private const string AlbumSlotType = "JELLYFIN_ALBUM";

    private static readonly Dictionary<CatalogType, string> SlotTypeSignatures = CatalogSlotTypes.Names;

    private readonly ILibraryManager _libraryManager;
    private readonly CatalogManager _catalogManager;
    private readonly ILogger<LibrarySyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="catalogManager">SMAPI catalog manager.</param>
    /// <param name="logger">Logger instance.</param>
    public LibrarySyncService(
        ILibraryManager libraryManager,
        CatalogManager catalogManager,
        ILogger<LibrarySyncService> logger)
    {
        _libraryManager = libraryManager;
        _catalogManager = catalogManager;
        _logger = logger;
    }

    /// <summary>
    /// Sync a user's library to SMAPI catalogs.
    /// Creates catalogs if they don't exist, updates them if they do.
    /// </summary>
    /// <param name="user">The Jellyfin user with SMAPI credentials.</param>
    /// <param name="jellyfinUser">The Jellyfin user entity for library queries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what was synced.</returns>
    public async Task<SyncResult> SyncUserLibraryAsync(
        Entities.User user,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult();

        if (user.SmapiDeviceToken == null || user.UserSkill?.SkillId == null)
        {
            _logger.LogWarning("Skipping catalog sync for user {UserId}: no SMAPI token or skill ID", user.Id);
            return result;
        }

        if (string.IsNullOrEmpty(user.SmapiDeviceToken.VendorId))
        {
            _logger.LogWarning("Skipping catalog sync for user {UserId}: no vendor ID configured", user.Id);
            return result;
        }

        string accessToken = user.SmapiDeviceToken.AccessToken;
        string vendorId = user.SmapiDeviceToken.VendorId;

        // Sync artists and albums in parallel
        var artistTask = SyncCatalogAsync(
            user, jellyfinUser, accessToken, vendorId, CatalogType.Artist,
            BaseItemKind.MusicArtist,
            user.ArtistCatalogId,
            "Jellyfin Artists",
            "Artist catalog synced from Jellyfin library",
            cancellationToken);

        var albumTask = SyncCatalogAsync(
            user, jellyfinUser, accessToken, vendorId, CatalogType.Album,
            BaseItemKind.MusicAlbum,
            user.AlbumCatalogId,
            "Jellyfin Albums",
            "Album catalog synced from Jellyfin library",
            cancellationToken);

        await Task.WhenAll(artistTask, albumTask).ConfigureAwait(false);

        result.ArtistCount = await artistTask.ConfigureAwait(false);
        result.AlbumCount = await albumTask.ConfigureAwait(false);
        result.Success = true;
        result.SyncTime = DateTime.UtcNow;

        _logger.LogInformation("Catalog sync completed for user {UserId}: {Artists} artists, {Albums} albums",
            user.Id, result.ArtistCount, result.AlbumCount);

        return result;
    }

    private async Task<int> SyncCatalogAsync(
        Entities.User user,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        string accessToken,
        string vendorId,
        CatalogType catalogType,
        BaseItemKind itemKind,
        string? existingCatalogId,
        string catalogName,
        string catalogDescription,
        CancellationToken cancellationToken)
    {
        // Fetch items from library
        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true)
        });

        if (items.Count == 0)
        {
            _logger.LogInformation("No {Type} items found in library for user {UserId}", catalogType, user.Id);
            return 0;
        }

        // Limit items before generating synonyms to avoid wasted work
        var itemTuples = items
            .Select(i => (i.Id, i.Name))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Take(MaxCatalogValues);

        CatalogPayload payload = CatalogPayload.FromItems(catalogType, itemTuples, PhoneticSynonymGenerator.GenerateSynonyms);

        if (payload.Values.Count >= MaxCatalogValues)
        {
            _logger.LogWarning("Truncated {Type} catalog to {Limit} items for user {UserId}",
                catalogType, MaxCatalogValues, user.Id);
        }

        string catalogId;

        if (string.IsNullOrEmpty(existingCatalogId))
        {
            string slotTypeSignature = SlotTypeSignatures[catalogType];

            catalogId = await _catalogManager.CreateCatalogAsync(
                accessToken, vendorId, catalogName, catalogDescription,
                slotTypeSignature, cancellationToken).ConfigureAwait(false);

            if (catalogType == CatalogType.Artist)
            {
                user.ArtistCatalogId = catalogId;
            }
            else
            {
                user.AlbumCatalogId = catalogId;
            }
        }
        else
        {
            catalogId = existingCatalogId;
        }

        // Upload values
        string version = await _catalogManager.UploadCatalogValuesAsync(
            accessToken, catalogId, payload, cancellationToken).ConfigureAwait(false);

        // Create or update slot type referencing the catalog
        string slotTypeName = catalogType == CatalogType.Artist ? ArtistSlotType : AlbumSlotType;
        await _catalogManager.CreateOrUpdateSlotTypeAsync(
            accessToken, vendorId, slotTypeName, catalogId, cancellationToken).ConfigureAwait(false);

        // Trigger model update for Italian locale
        if (user.UserSkill?.SkillId != null)
        {
            try
            {
                await _catalogManager.TriggerModelUpdateAsync(
                    accessToken, user.UserSkill.SkillId, DevelopmentStage, ItalianLocale,
                    catalogId, version, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to trigger model update for catalog {CatalogId}", catalogId);
            }
        }

        return payload.Values.Count;
    }

    /// <summary>
    /// Result of a catalog sync operation.
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public DateTime SyncTime { get; set; }
        public int ArtistCount { get; set; }
        public int AlbumCount { get; set; }
    }
}
