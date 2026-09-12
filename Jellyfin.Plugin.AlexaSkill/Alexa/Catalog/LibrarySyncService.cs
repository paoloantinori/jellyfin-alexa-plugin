#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Lwa;
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
        /// <summary>
        /// JF-544: a 17-locale sync runs 30-45 min against ~1h LWA access tokens, so the
        /// sync must not start on a token with less than this much life left; the
        /// TokenRefreshTask safety margin (30 min) protects short ops, not this one.
        /// </summary>
        private const int SyncTokenBudgetMinutes = 45;

    private const int MaxCatalogValues = 50000;
    private const string DevelopmentStage = "development";
    private const string DefaultLocale = "it-IT";
    private const int InterLocaleDelayMs = 2000;

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

        if (string.IsNullOrEmpty(user.VendorId))
        {
            _logger.LogWarning("Skipping catalog sync for user {UserId}: no vendor ID configured", user.Id);
            return result;
        }

        // JF-544: refresh up front unless comfortably more than the whole-sync budget
        // remains (unknown expiry also refreshes; a failed refresh is not fatal, the
        // per-leg re-read and 401 retry below are the second and third lines of defense).
        TimeSpan tokenRemaining = SmapiTokenRefresher.RemainingLifetime(user);
        if (tokenRemaining < TimeSpan.FromMinutes(SyncTokenBudgetMinutes))
        {
            _logger.LogInformation(
                "Refreshing SMAPI token before catalog sync: {Minutes:F0} min remaining < {Budget} min sync budget (JF-544)",
                tokenRemaining.TotalMinutes, SyncTokenBudgetMinutes);
            if (!await SmapiTokenRefresher.RefreshAsync(user, _logger).ConfigureAwait(false))
            {
                _logger.LogWarning("Pre-sync token refresh failed for user {UserId}; proceeding with the current token", user.Id);
            }
        }

        string vendorId = user.VendorId;
        string skillId = user.UserSkill!.SkillId!;

        // Determine which locales to sync. JF-544: the CURRENT token, not a
        // start-of-sync snapshot, so the pre-sync refresh above is what this call uses.
        string syncLocalesConfig = Plugin.Instance?.Configuration?.CatalogSyncLocales ?? string.Empty;
        IReadOnlyList<string> resolvedLocales = await ResolveSyncLocalesAsync(
            syncLocalesConfig, user.SmapiDeviceToken.AccessToken, skillId, cancellationToken).ConfigureAwait(false);

        // JF-543: locales whose Amazon-side full build cannot host catalog-backed slot
        // types (evidence in CatalogManager.CatalogWiringUnsupportedLocales). Skipping
        // the whole leg: the catalog uploads and the model injection are worthless when
        // the resulting model cannot build, and each failed build leaves the locale's
        // live model FAILED until manually restored.
        var unsupported = resolvedLocales.Where(l => !CatalogManager.IsCatalogWiringSupported(l)).ToList();
        List<string> locales = resolvedLocales
            .Where(l => CatalogManager.IsCatalogWiringSupported(l))
            .ToList();
        if (unsupported.Count > 0)
        {
            _logger.LogInformation(
                "Catalog sync excludes {Locales}: Amazon's full build fails for catalog-wired models in these locales (JF-543). New syncs leave their live models untouched; a model already left FAILED by a pre-fix sync needs a one-time rebuild (Rebuild models, or the next version bump) to return to the embedded model",
                string.Join(", ", unsupported));
        }

        _logger.LogInformation("Catalog sync for user {UserId}: {LocaleCount} locales ({Locales})",
            user.Id, locales.Count, string.Join(", ", locales));

        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // Fetch library items once (shared across locales)
        var artistItems = FetchLibraryItems(jellyfinUser, user, BaseItemKind.MusicArtist);
        var albumItems = FetchLibraryItems(jellyfinUser, user, BaseItemKind.MusicAlbum);
        var seriesItems = FetchLibraryItems(jellyfinUser, user, BaseItemKind.Series);

        result.ArtistCount = artistItems.Count;
        result.AlbumCount = albumItems.Count;
        result.SeriesCount = seriesItems.Count;

        if (artistItems.Count == 0 && albumItems.Count == 0 && seriesItems.Count == 0)
        {
            _logger.LogWarning("No artists, albums or series found for user {UserId}, skipping sync", user.Id);
            return result;
        }

        int localesSucceeded = 0;
        int localesFailed = 0;

        // JF-544: the leg body takes the CURRENT token at call time, not a
        // start-of-sync snapshot; catalog version creation and the model PUT are both
        // safe to re-submit, which the one-shot 401 retry below relies on.
        async Task RunLegAsync(string locale, string token)
        {
            // Create/update catalogs with locale-specific phonetic synonyms
            var artistResult = await SyncCatalogForLocaleAsync(
                user, token, vendorId, CatalogType.Artist, artistItems,
                user.ArtistCatalogId, "Jellyfin Artists", "Artist catalog synced from Jellyfin library",
                locale, cancellationToken).ConfigureAwait(false);

            var albumResult = await SyncCatalogForLocaleAsync(
                user, token, vendorId, CatalogType.Album, albumItems,
                user.AlbumCatalogId, "Jellyfin Albums", "Album catalog synced from Jellyfin library",
                locale, cancellationToken).ConfigureAwait(false);

            var seriesResult = await SyncCatalogForLocaleAsync(
                user, token, vendorId, CatalogType.Series, seriesItems,
                user.SeriesCatalogId, "Jellyfin Series", "Series catalog synced from Jellyfin library",
                locale, cancellationToken).ConfigureAwait(false);

            // Update this locale's interaction model with the catalog references
            if (artistResult.Version != null || albumResult.Version != null || seriesResult.Version != null)
            {
                // JF-495: forward a catalog id ONLY together with the version minted
                // in THIS run. Forwarding a stored id with a null version (e.g. that
                // entity type had zero items this run) made the injection pin the
                // stale "1" fallback version; leaving the id null instead preserves
                // whatever catalog reference the live model already carries.
                var modelUpdate = await _catalogManager.UpdateInteractionModelAsync(
                    token,
                    skillId,
                    DevelopmentStage,
                    locale,
                    artistResult.Version != null ? user.ArtistCatalogId : null,
                    albumResult.Version != null ? user.AlbumCatalogId : null,
                    seriesResult.Version != null ? user.SeriesCatalogId : null,
                    artistResult.Version,
                    albumResult.Version,
                    seriesResult.Version,
                    cancellationToken).ConfigureAwait(false);

                // JF-495: catalog-sync model PUTs must appear in the per-locale
                // status ledger, not just ModelDeploymentManager deployments.
                RecordModelUpdateInLedger(locale, modelUpdate);
            }
        }

        foreach (string locale in locales)
        {
            var localeSw = System.Diagnostics.Stopwatch.StartNew();
            Exception? legError = null;
            var legSucceeded = false;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    // JF-544: the CURRENT token per attempt picks up any rotation by
                    // TokenRefreshTask instead of dying on the start-of-sync snapshot.
                    await RunLegAsync(locale, user.SmapiDeviceToken.AccessToken).ConfigureAwait(false);
                    legSucceeded = true;
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    legError = ex;
                    _logger.LogWarning(
                        "SMAPI 401 during catalog sync locale {Locale}: refreshing token and retrying the leg once (JF-544)",
                        locale);
                    string tokenBeforeRefresh = user.SmapiDeviceToken.AccessToken;
                    if (!await SmapiTokenRefresher.RefreshAsync(user, _logger).ConfigureAwait(false)
                        && tokenBeforeRefresh == user.SmapiDeviceToken.AccessToken)
                    {
                        // Our refresh failed AND nothing rotated the token meanwhile;
                        // a retry would re-send the same dead token. If the 20-min sweep
                        // DID rotate it while our call failed transiently, fall through
                        // and let attempt 2 use the fresh one.
                        break;
                    }
                }
                catch (Exception ex)
                {
                    legError = ex;
                    break;
                }
            }

            if (legSucceeded)
            {
                localesSucceeded++;
                _logger.LogInformation("Catalog sync locale {Locale} completed in {ElapsedMs}ms for user {UserId}",
                    locale, localeSw.ElapsedMilliseconds, user.Id);
            }
            else
            {
                localesFailed++;
                _logger.LogWarning(legError, "Catalog sync failed for locale {Locale}, user {UserId}; continuing with next locale",
                    locale, user.Id);
            }

            // Inter-locale delay to avoid SMAPI rate limits
            if (locales.Count > 1)
            {
                await Task.Delay(InterLocaleDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        totalSw.Stop();
        result.Success = localesSucceeded > 0;
        result.SyncTime = DateTime.UtcNow;

        _logger.LogInformation(
            "Catalog sync completed for user {UserId}: {Succeeded}/{Total} locales, {Artists} artists, {Albums} albums, {Series} series, {ElapsedMs}ms total",
            user.Id, localesSucceeded, locales.Count, result.ArtistCount, result.AlbumCount, result.SeriesCount, totalSw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// Ledger source label for interaction models pushed by the catalog sync's
    /// GET-modify-PUT path (JF-495), so those deployments are distinguishable from
    /// "Embedded" and "Custom" entries in LocaleModelStatuses.
    /// </summary>
    internal const string CatalogSyncLedgerSource = "CatalogSyncGetModifyPut";

    /// <summary>
    /// Records a catalog-sync model update outcome in the per-locale status ledger
    /// (JF-495). A canary mismatch lands in the entry's Error field so the admin UI
    /// surfaces it next to the build status.
    /// </summary>
    private void RecordModelUpdateInLedger(string locale, CatalogModelUpdateResult modelUpdate)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            config.SetLocaleModelStatus(locale, new Configuration.LocaleModelStatus
            {
                Status = modelUpdate.BuildStatus,
                LastUpdated = DateTime.UtcNow,
                Error = modelUpdate.CanaryError,
                Source = CatalogSyncLedgerSource
            });
            Plugin.Instance!.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record catalog-sync model update in the locale status ledger for {Locale} (non-fatal)",
                locale);
        }
    }

    /// <summary>
    /// Fetch library items of a given type, filtered by the user's allowed libraries.
    /// </summary>
    private IReadOnlyList<BaseItem> FetchLibraryItems(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        BaseItemKind itemKind)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true),
            Limit = MaxCatalogValues,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        // STRICT scope, no items-by-name bypass (includeItemsByName: false): this
        // feed becomes the persistent SMAPI catalog upload, so the bypass would let
        // excluded-library artist names live on Amazon's side indefinitely, not
        // just surface transiently in a cold-window search (JF-457). Rationale for
        // the automatic bypass itself: LibraryFilter.ApplyItemsByNameBypass (JF-456).
        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManager, includeItemsByName: false);

        return _libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Sync a catalog for a specific locale: build locale-specific payload with phonetic synonyms,
    /// upload to SMAPI, and return the version. Creates the catalog ID if it doesn't exist yet.
    /// </summary>
    private async Task<(int Count, string? Version)> SyncCatalogForLocaleAsync(
        Entities.User user,
        string accessToken,
        string vendorId,
        CatalogType catalogType,
        IReadOnlyList<BaseItem> items,
        string? existingCatalogId,
        string catalogName,
        string catalogDescription,
        string locale,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return (0, null);
        }

        var itemTuples = items
            .Select(i => (i.Id, i.Name))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name));

        CatalogPayload payload = CatalogPayload.FromItems(catalogType, itemTuples, PhoneticSynonymGenerator.GenerateSynonyms, locale);

        // JF-541 phase 2: merge the committed models' static seed values into the
        // upload. The catalog supplier replaces the static type block at deploy
        // time (CatalogManager.InjectCatalogReferences), so without this the seed
        // titles absent from the library (Thriller, Queen, ...) vanish from the
        // deployed model. Library entries win on collision; no-op for seed-less types.
        CatalogSeedEnrichment.MergeInto(payload, catalogType, PhoneticSynonymGenerator.GenerateSynonyms, locale, _logger);

        if (payload.Values.Count >= MaxCatalogValues)
        {
            _logger.LogWarning(
                "Truncated {Type} catalog to {Limit} items for user {UserId}",
                catalogType,
                MaxCatalogValues,
                user.Id);
        }

        string catalogId;

        if (string.IsNullOrEmpty(existingCatalogId))
        {
            catalogId = await _catalogManager.CreateCatalogAsync(
                accessToken,
                vendorId,
                catalogName,
                catalogDescription,
                cancellationToken).ConfigureAwait(false);

            if (catalogType == CatalogType.Artist)
            {
                user.ArtistCatalogId = catalogId;
            }
            else if (catalogType == CatalogType.Album)
            {
                user.AlbumCatalogId = catalogId;
            }
            else if (catalogType == CatalogType.Series)
            {
                user.SeriesCatalogId = catalogId;
            }
        }
        else
        {
            catalogId = existingCatalogId;
        }

        string payloadJson = JsonSerializer.Serialize(payload, CatalogManager.JsonOptions);
        string serverAddress = Plugin.Instance!.Configuration.ServerAddress.TrimEnd('/');

        // URL factory: re-store the payload on each call so a retry (after a transient
        // SMAPI fetch failure) gets a fresh, unconsumed source URL.
        Func<string> catalogUrlFactory = () =>
        {
            string cacheKey = CatalogController.StorePayload(payloadJson);
            return $"{serverAddress}/alexaskill/catalog/{cacheKey}";
        };

        string catalogVersion = await _catalogManager.UploadCatalogValuesAsync(
            accessToken,
            catalogId,
            payload,
            catalogUrlFactory,
            cancellationToken).ConfigureAwait(false);

        return (payload.Values.Count, catalogVersion);
    }

    /// <summary>
    /// Resolve which locales to sync based on the config string.
    /// - Empty: it-IT only (default).
    /// - "*": all active locales (from SMAPI manifest).
    /// - "de-DE,en-US,...": it-IT + the listed locales.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ResolveSyncLocalesAsync(
        string syncLocalesConfig,
        string accessToken,
        string skillId,
        CancellationToken cancellationToken)
    {
        var result = new List<string> { DefaultLocale }; // it-IT always included

        if (string.IsNullOrWhiteSpace(syncLocalesConfig))
        {
            return result;
        }

        if (syncLocalesConfig.Trim() == "*")
        {
            var active = await GetActiveLocalesAsync(accessToken, skillId, cancellationToken).ConfigureAwait(false);
            foreach (string locale in active)
            {
                if (!result.Contains(locale, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(locale);
                }
            }

            return result;
        }

        // Explicit comma-separated list
        foreach (string raw in syncLocalesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!result.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(raw);
            }
        }

        return result;
    }

    /// <summary>
    /// Get the locales the skill declares support for (from the manifest's PublishingInformation).
    /// Falls back to it-IT if the SMAPI call fails.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetActiveLocalesAsync(
        string accessToken,
        string skillId,
        CancellationToken cancellationToken)
    {
        try
        {
            var smapi = new SmapiManagement(
                new DeviceToken(accessToken, string.Empty, "Bearer", 0),
                Plugin.Instance!.LoggerFactory);

            // GetSkillAsync returns the manifest, which includes PublishingInformation.Locales
            // — a dictionary keyed by locale string (e.g. "it-IT", "en-US").
            var skillData = await smapi.GetSkillAsync(skillId).ConfigureAwait(false);
            var locales = skillData?.Manifest?.PublishingInformation?.Locales?.Keys.ToList();

            if (locales is null || locales.Count == 0)
            {
                _logger.LogWarning("GetActiveLocalesAsync: no locales returned from SMAPI manifest, falling back to it-IT");
                return new List<string> { DefaultLocale };
            }

            _logger.LogDebug("GetActiveLocalesAsync: {Count} active locales: {Locales}", locales.Count, string.Join(", ", locales));
            return locales;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // JF-544: a 401 here means the token is dead; degrading to it-IT would
            // run one locale, stamp the sync successful, and gate the other locales
            // out for the full 12h window. Fail loudly instead.
            _logger.LogWarning(ex, "GetActiveLocalesAsync got 401: token expired; failing the sync instead of silently syncing it-IT only (JF-544)");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActiveLocalesAsync failed, falling back to it-IT only");
            return new List<string> { DefaultLocale };
        }
    }
}
