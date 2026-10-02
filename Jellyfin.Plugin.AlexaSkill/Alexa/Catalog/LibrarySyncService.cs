#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    /// Test seam (null in production, JF-695): invoked inside each catalog type's
    /// isolation try at leg entry, before the payload build. Lets the isolation
    /// pins simulate a deterministic payload-build invariant violation (throw
    /// <see cref="CatalogPayloadInvariantException"/>) that the real factory
    /// cannot produce by construction (AppendTo and AssertArtistEnrichment
    /// re-derive from the same Generate), and pin that non-invariant exceptions
    /// still propagate to the leg-level retry machinery.
    /// </summary>
    internal Action<CatalogType>? TypeLegEntryProbeForTest { get; set; }

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
        // remains; the per-leg re-read and 401 retry below are the second and third
        // lines of defense.
        await SmapiTokenRefresher.EnsureLifetimeBudgetAsync(
            user, SyncTokenBudgetMinutes, "catalog sync", _logger).ConfigureAwait(false);

        string vendorId = user.VendorId;
        string skillId = user.UserSkill!.SkillId!;

        // Determine which locales to sync (with the token the pre-sync gate left us).
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

        // JF-544: every SMAPI call in the leg reads the CURRENT token (see the
        // per-attempt re-read below); catalog version creation and the model PUT are
        // both safe to re-submit, which the one-shot 401 retry relies on.
        // JF-513.3 (item 2): locale legs whose payload is identical to one already
        // (the byte-identical legs are the synonym-equivalence classes the
        // JF-709 audit names: es x3, fr x2, and the 6-member en/hi cluster;
        // ar-SA never reaches the sync, the JF-543 filter above.)
        // uploads are byte-identical to a previous leg's) are skipped: SMAPI stores
        // a new catalog version per upload, so re-minting identical content burns
        // quota and the 17-locale volume is the growth this item flagged. The
        // version returned for the skipped type is null, so the injection below
        // treats it exactly like a zero-item type (no id forwarded), and a leg
        // whose types ALL skipped performs no model PUT at all; see JF-709 for
        // the no-generator-locale consequence of that shape.
        Dictionary<string, string> uploadedPayloadHashes = new(StringComparer.Ordinal);

        // JF-706: the ONE per-type wiring table for the whole sync (locale-
        // invariant, built once; the getters read the user's stored catalog ids
        // live). The per-leg loop inside RunLegAsync, the injection gate, and
        // the per-type id/version extraction at the UpdateInteractionModelAsync
        // call all derive from this list, collapsing three parallel call
        // sites, the gate, and six positional injection arguments (the repo's
        // "missed one" bug class: a missed site compiles clean and freezes or
        // syncs inconsistently). The catalog id is a GETTER, not a snapshot:
        // SyncCatalogForLocaleAsync assigns it when it creates the catalog, so
        // it is read live at the leg call and re-read right after the leg
        // returns (final from there: a leg writes only its own type's id). A
        // fourth catalog type is a one-row edit HERE plus its write-back
        // branch in SyncCatalogForLocaleAsync (still per-type there, tracked
        // as JF-711).
        var typeLegs = new (CatalogType Type, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, string Name, string Description)[]
        {
            (CatalogType.Artist, artistItems, () => user.ArtistCatalogId, "Jellyfin Artists", "Artist catalog synced from Jellyfin library"),
            (CatalogType.Album, albumItems, () => user.AlbumCatalogId, "Jellyfin Albums", "Album catalog synced from Jellyfin library"),
            (CatalogType.Series, seriesItems, () => user.SeriesCatalogId, "Jellyfin Series", "Series catalog synced from Jellyfin library"),
        };

        async Task<IReadOnlyList<CatalogType>> RunLegAsync(string locale)
        {
            var frozenTypes = new List<CatalogType>();

            // JF-695 per-type isolation: ONE try per catalog type, so a
            // deterministic payload-build invariant failure
            // (CatalogPayloadInvariantException, e.g. the JF-689 enrichment guard
            // drifting inside CatalogPayload.FromItems or the seed merge) freezes
            // ONLY its own type: no version is minted, so the model injection
            // below forwards a null id for it and the live model keeps that
            // type's last-good pinned catalog reference. The freeze itself is the
            // JF-689 contract kept verbatim (per-entry degradation would ship
            // incomplete catalogs); JF-695 only narrowed the blast radius from
            // the whole locale leg to the type. Every other failure keeps the
            // whole-leg handling in the locale attempt loop below (the 401
            // refresh-retry, transient-fetch exhaustion, timeouts): catching
            // those here would swallow the leg-level retry semantics.
            async Task<string?> SyncTypeLegAsync(
                CatalogType catalogType,
                IReadOnlyList<BaseItem> items,
                string? existingCatalogId,
                string catalogName,
                string catalogDescription)
            {
                try
                {
                    // Test seam, null in production (see TypeLegEntryProbeForTest):
                    // fires inside the isolation try so the pins can simulate the
                    // drifted payload-build invariant the real factory cannot
                    // produce by construction. Guarded on items.Count > 0 so the
                    // seam's reachability equals the real throw's (a zero-item
                    // type never builds a payload, so it can never freeze).
                    if (items.Count > 0)
                    {
                        TypeLegEntryProbeForTest?.Invoke(catalogType);
                    }

                    return (await SyncCatalogForLocaleAsync(
                        user, user.SmapiDeviceToken.AccessToken, vendorId, catalogType, items,
                        existingCatalogId, catalogName, catalogDescription,
                        locale, uploadedPayloadHashes, cancellationToken).ConfigureAwait(false)).Version;
                }
                catch (CatalogPayloadInvariantException ex)
                {
                    _logger.LogError(ex,
                        "Catalog {CatalogType} payload build violated a construction invariant for locale {Locale}, user {UserId}; freezing the type for this run (its last-good version stays pinned and its catalog id is not forwarded to the model injection) while the remaining types continue (JF-695)",
                        catalogType, locale, user.Id);
                    frozenTypes.Add(catalogType);
                    return null;
                }
            }

            // Create/update catalogs with locale-specific phonetic synonyms.
            // Each minted entry pairs the fresh version with the catalog id as
            // it stands right after the leg; types that minted no version
            // (frozen, zero items, or the JF-513.3 identical-payload skip) are
            // simply absent, which is the JF-495 null-id-with-null-version
            // rule the injection reads below.
            var minted = new Dictionary<CatalogType, (string? Version, string? CatalogId)>();
            foreach (var leg in typeLegs)
            {
                string? version = await SyncTypeLegAsync(
                    leg.Type, leg.Items, leg.StoredCatalogId(), leg.Name, leg.Description).ConfigureAwait(false);
                if (version != null)
                {
                    minted[leg.Type] = (version, leg.StoredCatalogId());
                }
            }

            // Update this locale's interaction model with the catalog references.
            // A frozen type's null version keeps its id out (the same JF-495
            // null-version rule the zero-items shape uses), so its last-good
            // catalog reference survives in the live model.
            if (minted.Count > 0)
            {
                // JF-495: forward a catalog id ONLY together with the version minted
                // in THIS run. Forwarding a stored id with a null version (e.g. that
                // entity type had zero items this run) made the injection pin the
                // stale "1" fallback version; leaving the id null instead preserves
                // whatever catalog reference the live model already carries.
                // JF-706: CatalogManager keeps per-type id/version parameters (not
                // a dictionary), so the arguments are extracted per type at this
                // ONE place from the minted collection; an absent type reads as
                // (null, null), which is that same rule made structural.
                (string? Version, string? CatalogId) Minted(CatalogType type) => minted.GetValueOrDefault(type);

                var modelUpdate = await _catalogManager.UpdateInteractionModelAsync(
                    user.SmapiDeviceToken.AccessToken,
                    skillId,
                    DevelopmentStage,
                    locale,
                    Minted(CatalogType.Artist).CatalogId,
                    Minted(CatalogType.Album).CatalogId,
                    Minted(CatalogType.Series).CatalogId,
                    Minted(CatalogType.Artist).Version,
                    Minted(CatalogType.Album).Version,
                    Minted(CatalogType.Series).Version,
                    cancellationToken).ConfigureAwait(false);

                // JF-495: catalog-sync model PUTs must appear in the per-locale
                // status ledger, not just ModelDeploymentManager deployments.
                // JF-705: the leg's frozen types ride the entry too.
                RecordModelUpdateInLedger(locale, modelUpdate, frozenTypes);
            }
            else if (frozenTypes.Count > 0)
            {
                // JF-709: the all-frozen leg performed no PUT, so without this
                // leg-boundary write the locale keeps the PREVIOUS run's green
                // SUCCEEDED ledger row. A no-PUT leg with ZERO frozen types
                // (zero-items / byte-identical hash-skips) deliberately writes
                // nothing; the starved-locale product gap is JF-717.
                RecordNoPutFrozenLegInLedger(locale, frozenTypes);
            }

            return frozenTypes;
        }

        foreach (string locale in locales)
        {
            var localeSw = System.Diagnostics.Stopwatch.StartNew();
            Exception? legError = null;
            var legSucceeded = false;
            IReadOnlyList<CatalogType>? legFrozenTypes = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    legFrozenTypes = await RunLegAsync(locale).ConfigureAwait(false);
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

                // JF-695: a leg that completed with frozen types still pinned its
                // healthy types' versions, but the freeze must not read as a
                // clean locale completion. Types frozen on a retried-away attempt
                // refreeze deterministically on the next attempt, so only the
                // returned (winning) attempt's list is merged here.
                if (legFrozenTypes is { Count: > 0 })
                {
                    foreach (CatalogType frozen in legFrozenTypes)
                    {
                        result.RecordFrozenType(frozen);
                    }

                    _logger.LogWarning(
                        "Catalog sync locale {Locale} completed with frozen catalog types ({Types}) for user {UserId}; each frozen type keeps its last-good pinned version (JF-695)",
                        locale, string.Join(", ", legFrozenTypes), user.Id);
                }
                else
                {
                    _logger.LogInformation("Catalog sync locale {Locale} completed in {ElapsedMs}ms for user {UserId}",
                        locale, localeSw.ElapsedMilliseconds, user.Id);
                }
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

        // JF-695: partial failure is honest. Any frozen type (see the RunLegAsync
        // isolation try for the full rationale) fails the run even though the
        // healthy types pinned their versions, so CatalogSyncTask does not stamp
        // LastCatalogSync over a payload build that will fail identically on
        // every subsequent run. ACCEPTED CADENCE COST (code-review): while the
        // drift persists, Success stays false and LastCatalogSync never
        // advances, so the startup trigger re-runs the FULL sync on every
        // restart and the healthy types mint fresh catalog versions each time
        // (pre-JF-695 the same drift aborted every leg at the artist payload
        // build with zero SMAPI writes). The feature is that the healthy types
        // keep pinning; the drift is a code bug to fix promptly, not a steady
        // state to optimize for.
        result.Success = localesSucceeded > 0 && result.FrozenTypes.Count == 0;

        result.SyncTime = DateTime.UtcNow;

        if (result.FrozenTypes.Count > 0)
        {
            _logger.LogError(
                "Catalog sync for user {UserId} finished with frozen catalog types ({Types}); their last-good versions stay pinned but will not refresh until the payload-build invariant violation is fixed (JF-695)",
                user.Id, string.Join(", ", result.FrozenTypes));
        }

        // The completion line must not read clean on a freeze run: a tail-grep
        // triage sees this line last, so the frozen types ride it too.
        string frozenClause = result.FrozenTypes.Count > 0
            ? $" (partial: {string.Join(", ", result.FrozenTypes)} frozen, Success=false)"
            : string.Empty;
        _logger.LogInformation(
            "Catalog sync completed for user {UserId}: {Succeeded}/{Total} locales, {Artists} artists, {Albums} albums, {Series} series, {ElapsedMs}ms total{FrozenClause}",
            user.Id, localesSucceeded, locales.Count, result.ArtistCount, result.AlbumCount, result.SeriesCount, totalSw.ElapsedMilliseconds, frozenClause);

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
    /// surfaces it next to the build status. JF-705: a partially frozen leg (its
    /// model PUT succeeded while one catalog type froze) keeps Status as the PUT's
    /// own outcome and composes a frozen-types clause into Error, ahead of any
    /// canary message (config.html truncates Error at 80 chars visually and renders
    /// it unconditionally next to the status icon, and the diagnostics panel's
    /// status-string matching of SUCCEEDED/FAILED/TIMEOUT stays valid). A distinct
    /// status value was rejected because the panel's ModelsDeployed checklist
    /// matches Status == "Succeeded" and would read false for a locale whose model
    /// build actually succeeded.
    /// </summary>
    private void RecordModelUpdateInLedger(
        string locale,
        CatalogModelUpdateResult modelUpdate,
        List<CatalogType> frozenTypes)
    {
        string? error = modelUpdate.CanaryError;
        if (frozenTypes.Count > 0)
        {
            // The frozen clause LEADS the combined message (rationale and the
            // 80-char truncation constraint live on FrozenLedgerClause).
            string frozenClause = FrozenLedgerClause(frozenTypes);
            error = string.IsNullOrEmpty(error) ? frozenClause : $"{frozenClause}; {error}";
        }

        WriteLedgerEntry(locale, new Configuration.LocaleModelStatus
        {
            Status = modelUpdate.BuildStatus,
            LastUpdated = DateTime.UtcNow,
            Error = error,
            Source = CatalogSyncLedgerSource
        });
    }

    /// <summary>
    /// The ONE ledger-write shell (guard, set, save, non-fatal catch) shared by
    /// both ledger writers (the JF-705 PUT path above and the JF-709 no-PUT
    /// path); the writers differ only in how the entry's fields are derived.
    /// </summary>
    private void WriteLedgerEntry(string locale, Configuration.LocaleModelStatus entry)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            config.SetLocaleModelStatus(locale, entry);
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
    /// The ledger-side frozen-types clause, shared by BOTH ledger writers so the
    /// entry text cannot drift (JF-705 PUT path and JF-709 no-PUT path). The
    /// run-level surfaces (LogError, the completion line) keep their own wordings
    /// per the JF-705 code-review decision. THE 80-CHAR CONSTRAINT LIVES HERE:
    /// config.html truncates Error at 80 chars visually (full text in the
    /// tooltip), so this clause must stay short and lead any appended context in
    /// every composition site.
    /// </summary>
    private static string FrozenLedgerClause(List<CatalogType> frozenTypes) =>
        $"{string.Join(" + ", frozenTypes)} catalog{(frozenTypes.Count > 1 ? "s" : string.Empty)} FROZEN (last-good pinned)";

    /// <summary>
    /// Records an ALL-FROZEN leg (no version minted for any type, no model PUT)
    /// in the per-locale status ledger (JF-709), replacing the locale's previous
    /// row rather than leaving it stale. The previous entry's Status is
    /// preserved verbatim when one exists: the live model on Amazon is UNCHANGED
    /// by this run, so its recorded build status is still true, and keeping it
    /// keeps the diagnostics panel's ModelsDeployed (Any Status=="Succeeded")
    /// truthful for single-locale setups. With no previous entry (fresh install,
    /// everything frozen on the first run) the entry reads "Skipped", whose
    /// documented meaning this extends to the no-PUT leg shapes. The freeze
    /// clause LEADS the Error (the live, actionable condition) and the previous
    /// entry's Error, when present, trails as "previous: ..." so a preserved
    /// FAILED status does not lose its failure diagnostic (code-review F3).
    /// Source is always this writer's own label: catalog sync authors THIS row.
    /// </summary>
    private void RecordNoPutFrozenLegInLedger(string locale, List<CatalogType> frozenTypes)
    {
        // The previous-entry READ sits under the same non-fatal contract as the
        // write (WriteLedgerEntry below): the startup capture can swap ledger
        // rows concurrently, and a Collection mutation during the
        // GetLocaleModelStatus scan must not fail a sync leg that otherwise
        // completed.
        try
        {
            Configuration.LocaleModelStatus? previous =
                Plugin.Instance?.Configuration?.GetLocaleModelStatus(locale);
            string error = $"{FrozenLedgerClause(frozenTypes)}; no PUT this run";
            if (previous?.Error is { Length: > 0 } previousError)
            {
                error = $"{error}; previous: {previousError}";
            }

            WriteLedgerEntry(locale, new Configuration.LocaleModelStatus
            {
                Status = previous?.Status ?? "Skipped",
                LastUpdated = DateTime.UtcNow,
                Error = error,
                Source = CatalogSyncLedgerSource
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record the all-frozen no-PUT leg in the locale status ledger for {Locale} (non-fatal)",
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
        Dictionary<string, string> uploadedPayloadHashes,
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

        // JF-513.3: skip the version upload when this exact payload was already
        // minted in this run (same user/catalog type across locale legs). A skip
        // reports Version null so the caller treats this leg as no-op for that
        // type. The hash is recorded only AFTER a successful upload (JF-703
        // addendum): recording before it made "already uploaded this run" really
        // mean "already attempted", so a failed upload followed by the leg-level
        // 401 retry hash-skipped a version that was never minted and dropped a
        // needed upload for the rest of the run. The trade: a version minted on
        // SMAPI but lost to a post-mint failure (timeout after acceptance) now
        // re-uploads on the retry, burning one duplicate version - the narrow,
        // self-healing direction to be wrong in.
        string payloadHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payloadJson)));
        string hashKey = $"{catalogType}:{catalogId}";
        if (uploadedPayloadHashes.TryGetValue(hashKey, out var seenHash) && seenHash == payloadHash)
        {
            _logger.LogInformation(
                "Catalog {Type} payload for user {UserId} locale {Locale} is identical to a payload already uploaded this run; skipping the version upload",
                catalogType,
                user.Id,
                locale);
            return (payload.Values.Count, null);
        }

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

        // Only a SUCCESSFUL upload records the hash (JF-703 addendum; full
        // rationale at the skip check above).
        uploadedPayloadHashes[hashKey] = payloadHash;

        return (payload.Values.Count, catalogVersion);
    }

    /// <summary>
    /// Resolve which locales to sync based on the config string.
    /// - "*": all active locales (from SMAPI manifest; the CONFIG default for
    ///   CatalogSyncLocales is "*", with the JF-543 ar-SA exclusion applied by
    ///   the caller).
    /// - Empty: it-IT only.
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
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
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
