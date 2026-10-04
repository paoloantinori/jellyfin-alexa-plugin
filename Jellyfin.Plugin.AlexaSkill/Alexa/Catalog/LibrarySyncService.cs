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
    /// Test seam (null in production, the TypeLegEntryProbeForTest pattern):
    /// overrides the inter-locale rate-limit delay so the multi-locale pins
    /// (JF-717) skip the 2s sleeps against a fake backend that needs no rate
    /// limiting. Zero disables the delay outright.
    /// </summary>
    internal int? InterLocaleDelayMsForTest { get; set; }

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

        // JF-706: the ONE per-type wiring table for the whole sync (locale-
        // invariant, built once; the accessors read and write the user's
        // stored catalog ids live). The per-leg loop inside RunLegAsync, the
        // injection gate, the per-type id/version extraction at the
        // UpdateInteractionModelAsync call, (JF-711) the stored-id write-back
        // inside SyncCatalogForLocaleAsync, and (JF-727) the item sourcing
        // loop right below all derive from this list, collapsing three
        // parallel call sites, the gate, six positional injection arguments,
        // the per-type id assignment, and the hand block of three fetches
        // with its count assignments and emptiness conjunction (the repo's
        // "missed one" bug class: a missed site compiles clean and freezes
        // or syncs inconsistently). The catalog id is a GETTER, not a
        // snapshot: SyncCatalogForLocaleAsync stores a newly created id
        // through the getter's paired SETTER, so it is read live at the leg
        // call and re-read right after the leg returns (final from there: a
        // leg writes only its own type's id). JF-711: the setter is a
        // required tuple element, so a fourth catalog type is a one-row edit
        // HERE (plus, outside this file, the CatalogSlotTypeNames forward
        // entry in CatalogSlotTypes that the injection and the graft
        // extraction key on: forgetting it leaves the fourth type synced but
        // never wired, surfacing only later as InjectCatalogReferences'
        // KeyNotFoundException mid-sync; and the SyncResult count property
        // the DTO boundary below owns) whose write-back, fetch, count, and
        // conjunction term exist by construction. The CatalogManager
        // per-type surface stays
        // deliberately outside this table (the JF-706 context boundary: a
        // real fourth synced type forces those edits loudly through
        // signature arity), and so does the SyncResult DTO's fixed per-type
        // count set (JF-727's deliberate stop: a public surface not worth
        // generalizing; the counts ride the table through StoreCount
        // instead). JF-727 folded the last two compile-silent per-type
        // sites, this sourcing loop and CatalogWiringGraft.ExtractWiring
        // (keyed off CatalogSlotTypes.CatalogSlotTypeNames), into
        // table-driven shapes.
        var typeLegs = new (CatalogType Type, BaseItemKind Kind, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, Action<string> StoreCatalogId, Action<int> StoreCount, string Name, string Description)[]
        {
            (CatalogType.Artist, BaseItemKind.MusicArtist, Array.Empty<BaseItem>(), () => user.ArtistCatalogId, id => user.ArtistCatalogId = id, count => result.ArtistCount = count, "Jellyfin Artists", "Artist catalog synced from Jellyfin library"),
            (CatalogType.Album, BaseItemKind.MusicAlbum, Array.Empty<BaseItem>(), () => user.AlbumCatalogId, id => user.AlbumCatalogId = id, count => result.AlbumCount = count, "Jellyfin Albums", "Album catalog synced from Jellyfin library"),
            (CatalogType.Series, BaseItemKind.Series, Array.Empty<BaseItem>(), () => user.SeriesCatalogId, id => user.SeriesCatalogId = id, count => result.SeriesCount = count, "Jellyfin Series", "Series catalog synced from Jellyfin library"),
        };

        // JF-727: item sourcing derives from the same table, replacing the
        // hand block of three fetches, three count assignments, and a
        // three-way emptiness conjunction. Each row's items are fetched once
        // (shared across locales) by the row's Kind, in row order (Artist,
        // Album, Series), preserving the pre-JF-727 fetch order so the
        // per-type item feeds, and therefore the payload bytes the JF-717
        // equivalence classes hash, are unchanged. The count lands through
        // the row's StoreCount element; the emptiness pre-check reads the
        // collection, so a fourth type's row cannot be silently excluded
        // from it: the pre-JF-727 missed CONJUNCTION term was behavioral (a
        // library holding only the missed type's items took the emptiness
        // skip and never synced), the missed count only cosmetic. The Items
        // placeholder is unconditionally replaced for every row before
        // anything reads it.
        for (int i = 0; i < typeLegs.Length; i++)
        {
            ref var leg = ref typeLegs[i];
            leg.Items = FetchLibraryItems(jellyfinUser, user, leg.Kind);
            leg.StoreCount(leg.Items.Count);
        }

        if (typeLegs.All(leg => leg.Items.Count == 0))
        {
            _logger.LogWarning("No artists, albums or series found for user {UserId}, skipping sync", user.Id);
            return result;
        }

        int localesSucceeded = 0;
        int localesFailed = 0;

        // JF-544: every SMAPI call in the leg reads the CURRENT token (see the
        // per-attempt re-read below); catalog version creation and the model PUT are
        // both safe to re-submit, which the one-shot 401 retry relies on.
        // JF-513.3 (item 2) + JF-717: the run-scoped map below memoizes, per
        // catalog payload ("{type}:{catalogId}:{payloadHash}"), the version
        // this run minted for it, so each byte-identical payload (the
        // synonym-prefix equivalence classes) uploads exactly once per run
        // regardless of locale order. The full dedup/wiring contract lives at
        // the skip check in SyncCatalogForLocaleAsync.
        Dictionary<string, string> mintedVersionsByPayload = new(StringComparer.Ordinal);

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
                Action<string> storeCatalogId,
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

                    return await SyncCatalogForLocaleAsync(
                        user, user.SmapiDeviceToken.AccessToken, vendorId, catalogType, items,
                        existingCatalogId, storeCatalogId, catalogName, catalogDescription,
                        locale, mintedVersionsByPayload, cancellationToken).ConfigureAwait(false);
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
            // Each entry pairs the minted version with the catalog id as it
            // stands right after the leg; types that minted no version (frozen
            // or zero items) are absent, which is the JF-495
            // null-id-with-null-version rule the injection reads below. A
            // byte-identical-payload skip contributes the class's shared mint
            // (JF-717), a version this run minted for this same catalog.
            var minted = new Dictionary<CatalogType, (string? Version, string? CatalogId)>();
            foreach (var leg in typeLegs)
            {
                string? version = await SyncTypeLegAsync(
                    leg.Type, leg.Items, leg.StoredCatalogId(), leg.StoreCatalogId, leg.Name, leg.Description).ConfigureAwait(false);
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
                // (all types zero-item; byte-identical hash-skips contribute a
                // shared version and PUT since JF-717) deliberately writes
                // nothing: there is genuinely no catalog state to report.
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

            // Inter-locale delay to avoid SMAPI rate limits; BETWEEN this run's
            // legs only (the final leg has no following call INSIDE the run to
            // space out; the spacing to another user's sync is
            // CatalogSyncTask's inter-user delay, JF-717).
            if (locales.Count > 1 && locale != locales[^1])
            {
                await Task.Delay(InterLocaleDelayMsForTest ?? InterLocaleDelayMs, cancellationToken).ConfigureAwait(false);
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
    /// surfaces it next to the build status. JF-705 (made structural by JF-721): a
    /// partially frozen leg (its model PUT succeeded while one catalog type froze)
    /// keeps Status as the PUT's own outcome and records the frozen types in the
    /// row's structured caveat (Caveat=FrozenCatalogs plus the names payload),
    /// which the admin UI renders beside the free-text Error; the canary keeps
    /// Error to itself, so nothing composes a clause into the diagnostic text
    /// anymore. A distinct status value was rejected because the panel's
    /// ModelsDeployed checklist matches Status == "Succeeded" and would read false
    /// for a locale whose model build actually succeeded.
    /// </summary>
    private void RecordModelUpdateInLedger(
        string locale,
        CatalogModelUpdateResult modelUpdate,
        List<CatalogType> frozenTypes)
    {
        var (caveat, frozenCatalogTypes) = FrozenCatalogCaveat(frozenTypes);
        WriteLedgerEntry(locale, new Configuration.LocaleModelStatus
        {
            Status = modelUpdate.BuildStatus,
            LastUpdated = DateTime.UtcNow,
            Error = modelUpdate.CanaryError,
            Caveat = caveat,
            FrozenCatalogTypes = frozenCatalogTypes,
            Source = CatalogSyncLedgerSource
        }, "catalog-sync model update");
    }

    /// <summary>
    /// The ledger-write shell (guard, set, save, non-fatal catch) shared by
    /// both catalog-sync ledger writers (the JF-705 PUT path above and the
    /// JF-709 no-PUT path); the writers differ only in how the entry's fields
    /// are derived. The startup capture (JF-710) is the third ledger writer
    /// and keeps its own hand-rolled write under a whole-capture catch, now
    /// with per-locale isolation inside it (JF-722: a malformed per-locale
    /// entry costs its own row, not every later locale's); the capture's
    /// JF-722 deferred refresh is the fourth writer, same family (it only
    /// rewrites capture-authored rows).
    /// The what label names the failed write in the non-fatal log line so triage
    /// reads the right surface.
    /// </summary>
    private void WriteLedgerEntry(string locale, Configuration.LocaleModelStatus entry, string what)
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
                "Failed to record the {What} in the locale status ledger for {Locale} (non-fatal)",
                what, locale);
        }
    }

    /// <summary>
    /// The ONE frozen-caveat factory for BOTH catalog-sync ledger writers
    /// (JF-705 PUT path and JF-709 no-PUT path): nothing froze -> (None, null);
    /// types froze -> (FrozenCatalogs, the names as a CSV "Artist,Album").
    /// Owning the pair here makes the bit-iff-payload invariant structural, so
    /// a future writer cannot set one without the other. The payload is display
    /// data only: the row's CaveatText renders it, nothing parses it.
    /// </summary>
    private static (Configuration.CatalogLedgerCaveats Caveat, string? FrozenCatalogTypes) FrozenCatalogCaveat(List<CatalogType> frozenTypes) =>
        frozenTypes.Count == 0
            ? (Configuration.CatalogLedgerCaveats.None, null)
            : (Configuration.CatalogLedgerCaveats.FrozenCatalogs, string.Join(",", frozenTypes));

    /// <summary>
    /// Records an ALL-FROZEN leg (no version minted for any type, no model PUT)
    /// in the per-locale status ledger (JF-709), replacing the locale's previous
    /// row rather than leaving it stale. Status: PreservedOrSkippedStatus (the
    /// policy and its consumer-weight rationale live on the helper). Caveat:
    /// FrozenCatalogs | NoCatalogPut plus the names payload. Error: the previous
    /// row's FREE-TEXT diagnostic carried VERBATIM (no framing), ONLY when the
    /// previous row was not authored by this subsystem's own shapes per
    /// <see cref="IsOwnShapeLedgerCaveat"/>: a foreign diagnostic (a JF-495
    /// canary mismatch, a failed-PUT reason, or an observation-era build-errors
    /// text) survives exactly ONE all-frozen run, because this write's own
    /// NoCatalogPut bit makes the NEXT all-frozen run replace the row wholesale
    /// instead of re-carrying it (the JF-709 rework F1 no-nesting rule, field-
    /// keyed since JF-721; the old "previous: " framing is gone with the marker
    /// family, so the carried text is the previous Error itself). Source is
    /// always this writer's own label: catalog sync authors THIS row.
    /// UPGRADE TRANSITION (accepted, JF-721 design): a pre-JF-721 persisted sync
    /// row carries no caveat bits, so its legacy COMPOSED Error (clause + tail +
    /// framing baked into the text) is field-indistinguishable from a bare
    /// foreign diagnostic and is carried once verbatim; the next all-frozen run
    /// drops it. Refusing to sniff the legacy text is the point of this task.
    /// </summary>
    private void RecordNoPutFrozenLegInLedger(string locale, List<CatalogType> frozenTypes)
    {
        // The previous-entry READ carries its own non-fatal guard (the write is
        // WriteLedgerEntry's): the startup capture can swap ledger rows
        // concurrently, and a Collection mutation during the GetLocaleModel
        // Status scan must not fail a sync leg that otherwise completed. A
        // failed read degrades to the no-previous-entry shape.
        Configuration.LocaleModelStatus? previous;
        try
        {
            previous = Plugin.Instance?.Configuration?.GetLocaleModelStatus(locale);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read the locale status ledger for {Locale} before recording the all-frozen no-PUT leg (non-fatal)",
                locale);
            previous = null;
        }

        string? carriedError = previous?.Error is { Length: > 0 } previousError
            && !IsOwnShapeLedgerCaveat(previous.Caveat)
                ? previousError
                : null;

        var (frozenCaveat, frozenCatalogTypes) = FrozenCatalogCaveat(frozenTypes);

        WriteLedgerEntry(locale, new Configuration.LocaleModelStatus
        {
            Status = PreservedOrSkippedStatus(previous?.Status),
            LastUpdated = DateTime.UtcNow,
            Error = carriedError,
            Caveat = frozenCaveat | Configuration.CatalogLedgerCaveats.NoCatalogPut,
            FrozenCatalogTypes = frozenCatalogTypes,
            Source = CatalogSyncLedgerSource
        }, "all-frozen no-PUT leg");
    }

    /// <summary>
    /// The no-PUT writer's Status clamp (rework F3): SETTLED statuses keep their
    /// consumer weight (SUCCEEDED feeds ModelsDeployed; FAILED/TIMEOUT keep the
    /// diagnostics panel's failedModels count; comparison is OrdinalIgnoreCase to
    /// match those consumers). Everything else clamps to the documented healthy
    /// neutral "Skipped": IN_PROGRESS (the startup capture's in-flight poll
    /// world) would freeze a momentary state into a permanent row, and UNVERIFIED
    /// (the catalog-sync PUT path's outcome-not-confirmed observation,
    /// CatalogManager) describes a superseded run whose reason still trails in
    /// Error when the prior row carried one.
    /// </summary>
    private static string PreservedOrSkippedStatus(string? previousStatus) =>
        string.Equals(previousStatus, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(previousStatus, "FAILED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(previousStatus, "TIMEOUT", StringComparison.OrdinalIgnoreCase)
            ? previousStatus!
            : "Skipped";

    /// <summary>
    /// Own-shape predicate for the no-PUT writer's carry decision (JF-709
    /// rework F1, extended by JF-710, made a FIELD read by JF-721): a previous
    /// row whose caveat carries FrozenCatalogs or NoCatalogPut was authored by
    /// this subsystem's own shapes (the frozen clause rode it, or it IS a
    /// previous no-PUT row), so its Error is REPLACED wholesale, never carried.
    /// The FrozenCatalogs arm is required because the startup capture preserves
    /// that bit onto its own rows (Source "Embedded"): a caveat-blind check
    /// would carry the preserved diagnostic forward forever while the skip-gated
    /// sync stays skipped (the JF-710 coordination-note hazard, field form).
    /// Accepted consequence (the JF-709 review's standing decision, kept): a
    /// foreign diagnostic riding a frozen row (a JF-705 canary after a freeze)
    /// is replaced rather than carried here; its durable home is the capture
    /// preserve. ObservedBuildErrors deliberately does NOT match: that Error is
    /// SMAPI's own text from the previous observation, foreign to this writer
    /// (and carried like any other foreign diagnostic).
    /// </summary>
    private static bool IsOwnShapeLedgerCaveat(Configuration.CatalogLedgerCaveats caveat) =>
        caveat.HasFlag(Configuration.CatalogLedgerCaveats.FrozenCatalogs)
        || caveat.HasFlag(Configuration.CatalogLedgerCaveats.NoCatalogPut);

    /// <summary>
    /// The surviving caveat fields of a JF-710 startup-capture preserve: what a
    /// CLEAN capture (no build errors of its own and a no-failure-weight state,
    /// SUCCEEDED or IN_PROGRESS; the capture owns that gate, widened to include
    /// IN_PROGRESS by JF-719) carries onto the row it writes instead of the old
    /// Error-text decomposition. A pure FIELD COPY (JF-721): no parsing, no
    /// literal vocabulary, nothing to drift.
    /// </summary>
    /// <param name="Caveat">The surviving caveat bits (NoCatalogPut already
    /// masked off; the run-scoped bit never survives a capture).</param>
    /// <param name="FrozenCatalogTypes">The surviving frozen-type names payload.</param>
    /// <param name="Error">The surviving free-text diagnostic, verbatim.</param>
    internal readonly record struct PreservedLedgerCaveat(
        Configuration.CatalogLedgerCaveats Caveat,
        string? FrozenCatalogTypes,
        string? Error);

    /// <summary>
    /// The JF-710 startup-capture preserve (JF-721's field form): called by the
    /// capture ONLY on a clean observation. A clean capture describes only the
    /// MODEL-BUILD surface, while a catalog-sync row's caveat fields and Error
    /// describe CATALOG state from the last sync, which a model rebuild does not
    /// reset (the next sync either re-freezes and rewrites them or heals and
    /// clears them), so they survive as a field copy. Recognition is
    /// field-keyed, not Source-keyed: a row authored by this subsystem
    /// (Source == CatalogSyncLedgerSource) preserves, and so does a row a
    /// PREVIOUS capture already preserved (that capture writes Source
    /// "Embedded", but its copied caveat bits are exactly what mark it ours, and
    /// the durable catalog state they name must survive every later capture
    /// while the skip-gated sync stays skipped; code-review F1). A bare foreign
    /// diagnostic without caveat bits rides its Source label and survives one
    /// capture cycle; a previous capture's own build error carries neither the
    /// Source label nor caveat bits and IS superseded by the fresh build, and an
    /// observed-errors row's Error is superseded the same way through
    /// <see cref="DropObservedBuildErrors"/> (its arm bit survives recognition
    /// only to be dropped with its Error).
    /// WHAT DROPS: the run-scoped NoCatalogPut bit (a new skill version WAS
    /// pushed, superseding the last run's no-PUT shape) and any
    /// ObservedBuildErrors-tagged Error with its bit
    /// (<see cref="DropObservedBuildErrors"/>, the one owner of that rule).
    /// UPGRADE TRANSITION (accepted, JF-721 design): a pre-JF-721 persisted
    /// CAPTURE-preserved row (Source "Embedded", legacy clause-led Error, no
    /// caveat bits) recognizes as neither catalog-authored nor caveat-carrying
    /// and clears on the first post-upgrade clean capture; a pre-JF-721 SYNC
    /// row preserves its legacy composed Error text verbatim (the tail and
    /// framing baked into the text ride along; they are text, not protocol).
    /// THIRD persisted shape (code-review F1): a pre-JF-721 capture/refresh
    /// own-errors row (Source "Embedded", Error "build errors: ...", no bits)
    /// also clears on the first post-upgrade clean capture, exactly like the
    /// clause-led shape, because the CAPTURE always rewrites every observed
    /// locale before the deferred refresh reads it (the pairing runs the
    /// capture first); the refresh's settle can only meet that legacy text on
    /// the race-composed path where the capture's per-locale write failed while
    /// the refresh's succeeds, and there it carries the stale text until the
    /// next capture or sync, the same next-writer bound as the other shapes.
    /// All shapes self-heal at the next catalog sync. Rejecting a legacy text
    /// sniffer is the point of this task.
    /// </summary>
    /// <param name="existing">The locale's current ledger entry, if any.</param>
    /// <returns>The surviving caveat fields, or null when nothing survives (and
    /// the capture's own clean Error stands).</returns>
    internal static PreservedLedgerCaveat? PreserveLedgerCaveatAcrossCapture(Configuration.LocaleModelStatus? existing)
    {
        if (existing == null)
        {
            return null;
        }

        // Own content, regardless of which writer last saved the row: the
        // catalog-sync source label, or this subsystem's caveat bits surviving
        // inside a capture-written row.
        bool catalogAuthored = string.Equals(existing.Source, CatalogSyncLedgerSource, StringComparison.Ordinal);
        if (!catalogAuthored && existing.Caveat == Configuration.CatalogLedgerCaveats.None)
        {
            return null;
        }

        // The field copy: durable bits survive, the run-scoped no-PUT bit and
        // any observation-era Error drop. The payload rides ONLY when its bit
        // survives (the FrozenCatalogCaveat factory's pair rule, enforced on
        // the copy path too, so a preserve can never mint a payload-without-bit
        // row; code-review F2: today no writer mints the mismatched input, the
        // guard makes that an invariant instead of an assumption).
        var (caveat, error) = DropObservedBuildErrors(existing.Caveat, existing.Error);
        caveat &= ~Configuration.CatalogLedgerCaveats.NoCatalogPut;
        return new PreservedLedgerCaveat(
            caveat,
            caveat.HasFlag(Configuration.CatalogLedgerCaveats.FrozenCatalogs) ? existing.FrozenCatalogTypes : null,
            error);
    }

    /// <summary>
    /// The ONE owner of the observation-era survival rule (JF-721): an
    /// ObservedBuildErrors-tagged Error and its bit never survive a later CLEAN
    /// observation, because the tagged errors describe an observation the clean
    /// one supersedes (possibly stale in the unverified SMAPI in-flight shape,
    /// JF-722 rework F1). Called by the startup capture's preserve (over the
    /// row it copies) and by the deferred refresh's clean-settle arm (over its
    /// own family row), so the drop cannot drift between the two writers the
    /// arm distinction exists for. Everything else about the row survives
    /// untouched; today an arm-tagged row carries nothing else (the own-errors
    /// arms replace wholesale), which is why this is a pure pair-drop.
    /// </summary>
    internal static (Configuration.CatalogLedgerCaveats Caveat, string? Error) DropObservedBuildErrors(
        Configuration.CatalogLedgerCaveats caveat,
        string? error) =>
        caveat.HasFlag(Configuration.CatalogLedgerCaveats.ObservedBuildErrors)
            ? (caveat & ~Configuration.CatalogLedgerCaveats.ObservedBuildErrors, null)
            : (caveat, error);

    /// <summary>
    /// The one formatter for a SMAPI per-locale build <c>Errors</c> array as it
    /// lands in a ledger/deploy-result Error field ("Code: Message; Code: Message";
    /// null for an empty or missing array). Shared by every observation writer
    /// (the startup capture and its JF-722 deferred refresh in SkillStartup, the
    /// redeployer's per-locale build result) so the format cannot drift between
    /// writers describing byte-identical observations; this subsystem's incidents
    /// are triaged from ledger forensics.
    /// </summary>
    /// <param name="errors">The observed build errors, if any.</param>
    /// <returns>The joined error text, or null when there is nothing to report.</returns>
    internal static string? FormatInvocationErrors(global::Alexa.NET.Management.Skills.InvocationError[]? errors) =>
        errors is { Length: > 0 }
            ? string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"))
            : null;

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
    /// upload to SMAPI, and return the minted version. Creates the catalog when no id is
    /// stored yet, persisting the new id through <paramref name="storeCatalogId"/> (the
    /// per-type wiring table's paired setter, JF-711). Returns null when the type has no
    /// items this run. A payload byte-identical to one
    /// already uploaded this run skips the upload and returns the version that earlier leg
    /// minted (JF-717), so the caller wires the equivalence class's shared
    /// (catalogId, version) pair into this locale's model.
    /// </summary>
    private async Task<string?> SyncCatalogForLocaleAsync(
        Entities.User user,
        string accessToken,
        string vendorId,
        CatalogType catalogType,
        IReadOnlyList<BaseItem> items,
        string? existingCatalogId,
        Action<string> storeCatalogId,
        string catalogName,
        string catalogDescription,
        string locale,
        Dictionary<string, string> mintedVersionsByPayload,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return null;
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

            // Fires only on creation; the JF-717 memo key below uses this same
            // local, and the caller's live re-read of the row's getter observes
            // this assignment (the setter itself is the wiring table's paired
            // row element; its rationale lives on the table, JF-711).
            storeCatalogId(catalogId);
        }
        else
        {
            catalogId = existingCatalogId;
        }

        string payloadJson = JsonSerializer.Serialize(payload, CatalogManager.JsonOptions);

        // JF-513.3: skip the version upload when this exact payload was already
        // minted in this run (same user/catalog type across locale legs). JF-717:
        // the skip returns the version the identical earlier leg minted instead of
        // null, so the caller's model PUT wires the class's shared catalog
        // reference; the pre-JF-717 null return starved every later class member
        // of its model PUT forever (its embedded model carries no valueCatalog
        // blocks, so catalog ER never activated there). The version is recorded
        // only AFTER a successful upload (JF-703 addendum): recording before it
        // made "already uploaded this run" really mean "already attempted", so a
        // failed upload followed by the leg-level 401 retry hash-skipped a
        // version that was never minted and dropped a needed upload for the rest
        // of the run. The trade: a version minted on SMAPI but lost to a
        // post-mint failure (timeout after acceptance) now re-uploads on the
        // retry, burning one duplicate version; the narrow, self-healing
        // direction to be wrong in. The payload hash rides the KEY, not the
        // value: a single per-catalog slot is order-sensitive (an interleaved
        // locale order evicts it on every class switch and re-mints identical
        // content), while keying by payload keeps the dedup at exactly one
        // upload per byte-identical payload per run.
        string payloadHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payloadJson)));
        string payloadKey = $"{catalogType}:{catalogId}:{payloadHash}";
        if (mintedVersionsByPayload.TryGetValue(payloadKey, out string? sharedVersion))
        {
            _logger.LogInformation(
                "Catalog {Type} payload for user {UserId} locale {Locale} is identical to a payload already uploaded this run; skipping the version upload and wiring the shared catalog version {Version} minted by the earlier locale leg (JF-717)",
                catalogType,
                user.Id,
                locale,
                sharedVersion);
            return sharedVersion;
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

        // Only a SUCCESSFUL upload records the minted version (JF-703 addendum;
        // full rationale at the skip check above).
        mintedVersionsByPayload[payloadKey] = catalogVersion;

        return catalogVersion;
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
