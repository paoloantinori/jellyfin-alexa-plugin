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

    /// <summary>
    /// How many dropped value names ride the JF-825 truncation warning: enough
    /// to identify both dropped categories (a library-tail name and a seed
    /// title) without flooding the log when a huge library drops a huge tail.
    /// </summary>
    private const int DroppedNameSampleSize = 5;
    private const string DevelopmentStage = "development";
    private const string DefaultLocale = "it-IT";
    private const int InterLocaleDelayMs = 2000;

    private readonly ILibraryManager _libraryManager;
    private readonly CatalogManager _catalogManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<LibrarySyncService> _logger;

    /// <summary>
    /// Test seam (null in production, JF-695): invoked inside each catalog type's
    /// isolation try at leg entry, before the payload build. Lets the isolation
    /// pins simulate a deterministic payload-build invariant violation (throw
    /// <see cref="CatalogPayloadInvariantException"/>) that the real factory
    /// cannot produce by construction (AppendTo and AssertPartialWordEnrichment
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
    /// Test seam (null in production, the TypeLegEntryProbeForTest pattern):
    /// overrides the per-type catalog-value cap so the JF-825 truncation pins
    /// can drive a full sync past the cap without materializing fifty
    /// thousand library items. Read by BOTH cap sites through
    /// <see cref="EffectiveMaxCatalogValues"/>: the fetch limit and the
    /// final-payload truncation, so the pinned cap governs the whole leg.
    /// </summary>
    internal int? MaxCatalogValuesForTest { get; set; }

    /// <summary>The cap the leg actually enforces: the test seam, else the const.</summary>
    private int EffectiveMaxCatalogValues => MaxCatalogValuesForTest ?? MaxCatalogValues;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="catalogManager">SMAPI catalog manager.</param>
    /// <param name="userDataManager">User data manager (JF-946: per-user play counts for the song-title ordering).</param>
    /// <param name="logger">Logger instance.</param>
    public LibrarySyncService(
        ILibraryManager libraryManager,
        CatalogManager catalogManager,
        IUserDataManager userDataManager,
        ILogger<LibrarySyncService> logger)
    {
        _libraryManager = libraryManager;
        _catalogManager = catalogManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// JF-737: the ONE emptiness predicate over the per-type wiring table,
    /// named (not inline in SyncUserLibraryAsync) so the JF-727 filing's one
    /// unpinned per-type fact is closed from both sides. The definition MUST
    /// stay the All() over the FULL table, never a hand conjunction over the
    /// first N rows: the filed hazard is a rewrite into a three-term
    /// conjunction over the current rows, after which a fourth synced type's
    /// row silently misses its term and a library holding only that type's
    /// items takes the emptiness skip and never syncs (the repo's "missed
    /// one" class). Two pins hold it: the call-site pin
    /// (AllTypeLegsEmpty_HasExactlyOneCallSite_TheEmptinessPreCheck) fails
    /// when the check is inlined back into the method, and the behavioral
    /// discriminator (LibrarySyncServiceEmptinessPrecheckTests) feeds this
    /// predicate a leg beyond the current three holding the library's only
    /// items, the input the sync itself cannot construct today, on which the
    /// hand conjunction and the All() derivation disagree. Placed directly
    /// above the sync entry point (code-review F3): the table's reader meets
    /// the predicate and this contract one screen before the initializer;
    /// a local function under the table would be unpinnable by the
    /// behavioral test. EMPTY-ARRAY CONTRACT (orchestrator gate-marker
    /// GM-F3): a zero-length table returns TRUE vacuously (All over an empty
    /// sequence), so a future refactor that derives the table dynamically
    /// instead of this method-local literal must not let an empty derivation
    /// silently take the skip. A guard throw was considered and DECLINED for
    /// now: the table is a literal, the shape is unreachable today, and a
    /// dynamic-derivation edit is exactly the change that must re-judge this
    /// pin battery, where this sentence lives.
    /// </summary>
    internal static bool AllTypeLegsEmpty(
        (CatalogType Type, BaseItemKind Kind, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, Action<string> StoreCatalogId, Action<int> StoreCount, string Name, string Description, Func<IReadOnlyList<BaseItem>> FetchItems)[] legs)
        => legs.All(leg => leg.Items.Count == 0);

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

        // JF-846: the maintenance window this sync opens (and every later exit
        // path closes via the using) drives the pipeline's SkillUpdatingPrefix:
        // spoken responses during the sync say the skill is updating. Opened
        // after the no-work skip guards so a window exists only while real
        // SMAPI maintenance is in flight; in a multi-user run the window
        // therefore closes during the task's inter-user delay gaps (nothing is
        // being maintained there, so the prefix honestly rests too).
        using var maintenanceScope = SkillMaintenanceScope.Open($"catalog sync (user {user.Id})", _logger);

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

        // JF-949 (JF-946 review F1): the opt-out must TEAR DOWN. A disabled
        // CatalogSongSyncEnabled with a stored SongCatalogId means a prior
        // opt-in left its artifacts behind: the SongTitle wiring (only
        // it-IT/de-DE declare the slot, but the wired block rides every synced
        // locale model that carried it, against the 1.5MB effective cap), the
        // SMAPI catalog itself (the uploaded tracklist, including the private
        // recordings the privacy gate exists to remove), and the stale stored
        // id. Strip the wiring FIRST (a model must never keep referencing a
        // catalog this run deletes), delete the catalog only when every strip
        // succeeded, and clear SongCatalogId only after the delete. A failed
        // step keeps the id so the next sync run retries the teardown. The
        // SongCatalogId read lives INSIDE TearDownSongCatalogAsync: the stored-id
        // structure pin allows the getter only in the wiring-table lambda, the
        // JF-826 suppression predicate, and the teardown reader.
        bool songTeardownFailed = !user.CatalogSongSyncEnabled
            && !await TearDownSongCatalogAsync(user, skillId, cancellationToken).ConfigureAwait(false);

        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // JF-706: the ONE per-type wiring table for the whole sync (locale-
        // invariant, built once; the accessors read and write the user's
        // stored catalog ids live). The per-leg loop inside RunLegAsync, the
        // injection gate, the per-type id/version extraction at the
        // UpdateInteractionModelAsync call, (JF-711) the stored-id write-back
        // inside SyncCatalogForLocaleAsync, and (JF-727) the item sourcing
        // loop right below all derive from this list, collapsing three
        // parallel call sites, the gate, eight positional injection arguments
        // (six before JF-823's fourth type),
        // the per-type id assignment, and the hand block of four fetches
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
        // conjunction term exist by construction. JF-823 exercised exactly
        // this: the Audiobook row below is that one-row edit. The CatalogManager
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
        var typeLegs = new (CatalogType Type, BaseItemKind Kind, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, Action<string> StoreCatalogId, Action<int> StoreCount, string Name, string Description, Func<IReadOnlyList<BaseItem>> FetchItems)[]
        {
            // JF-954.8: the Kind column and the generic rows' fetch kinds read
            // CatalogTypeItemKind, so the type-to-kind relation has ONE owner
            // shared with the snapshot export's fetch dispatch.
            (CatalogType.Artist, CatalogTypeItemKind(CatalogType.Artist), Array.Empty<BaseItem>(), () => user.ArtistCatalogId, id => user.ArtistCatalogId = id, count => result.ArtistCount = count, "Jellyfin Artists", "Artist catalog synced from Jellyfin library", () => FetchLibraryItems(jellyfinUser, user, CatalogTypeItemKind(CatalogType.Artist))),
            (CatalogType.Album, CatalogTypeItemKind(CatalogType.Album), Array.Empty<BaseItem>(), () => user.AlbumCatalogId, id => user.AlbumCatalogId = id, count => result.AlbumCount = count, "Jellyfin Albums", "Album catalog synced from Jellyfin library", () => FetchLibraryItems(jellyfinUser, user, CatalogTypeItemKind(CatalogType.Album))),
            (CatalogType.Series, CatalogTypeItemKind(CatalogType.Series), Array.Empty<BaseItem>(), () => user.SeriesCatalogId, id => user.SeriesCatalogId = id, count => result.SeriesCount = count, "Jellyfin Series", "Series catalog synced from Jellyfin library", () => FetchLibraryItems(jellyfinUser, user, CatalogTypeItemKind(CatalogType.Series))),
            // JF-823: the audiobook leg, the one-row edit the JF-711 note above
            // described.
            (CatalogType.Audiobook, CatalogTypeItemKind(CatalogType.Audiobook), Array.Empty<BaseItem>(), () => user.AudiobookCatalogId, id => user.AudiobookCatalogId = id, count => result.AudiobookCount = count, "Jellyfin Audiobooks", "Audiobook catalog synced from Jellyfin library", () => FetchLibraryItems(jellyfinUser, user, CatalogTypeItemKind(CatalogType.Audiobook))),
            // JF-874: the movie leg, feeding the catalog-backed MovieTitle slot
            // type (it-IT's PlayMovieIntent wrapper family). Same one-row shape.
            (CatalogType.Movie, CatalogTypeItemKind(CatalogType.Movie), Array.Empty<BaseItem>(), () => user.MovieCatalogId, id => user.MovieCatalogId = id, count => result.MovieCount = count, "Jellyfin Movies", "Movie catalog synced from Jellyfin library", () => FetchLibraryItems(jellyfinUser, user, CatalogTypeItemKind(CatalogType.Movie))),
            // JF-946: the song-title leg, feeding the catalog-backed SongTitle
            // slot type (the JF-940 PlayLibrarySong carrier). Its source never
            // rides the generic SortName fetch (alphabetical ordering would
            // cut the alphabet's tail at the cap, and the Limit would bind
            // before the stripped dedup): it fetches uncapped and runs the
            // preparation pipeline, whose output IS the item list (fresh
            // query instances; mutating Name to the stripped form writes
            // nothing back). GATED per user by the CatalogSongSyncEnabled
            // opt-in (privacy: the FULL tracklist goes to Amazon, including
            // private recordings); a disabled flag removes the row below, so
            // no Song catalog is created and none is wired into any locale.
            (CatalogType.Song, CatalogTypeItemKind(CatalogType.Song), Array.Empty<BaseItem>(), () => user.SongCatalogId, id => user.SongCatalogId = id, count => result.SongCount = count, "Jellyfin Songs", "Song title catalog synced from Jellyfin library", () => FetchAndPrepareSongTitles(jellyfinUser, user, result)),
        };

        // JF-946: the song leg participates only when the user opted in. The
        // reduced table keeps every downstream consumer (the sourcing loop,
        // AllTypeLegsEmpty, the locale legs) honest: a library holding ONLY
        // songs with the flag off takes the emptiness skip like any other
        // nothing-to-sync run. JF-954.8: the gate is the ONE named predicate
        // (<see cref="IsCatalogTypeSyncedForUser"/>) so the snapshot export's
        // roster gate cannot drift from this one.
        if (!user.CatalogSongSyncEnabled)
        {
            typeLegs = typeLegs.Where(leg => IsCatalogTypeSyncedForUser(leg.Type, user)).ToArray();
        }

        // JF-727: item sourcing derives from the same table, replacing the
        // hand block of three fetches, three count assignments, and a
        // three-way emptiness conjunction. Each row's items are fetched once
        // (shared across locales) by the row's Kind, in row order (Artist,
        // Album, Series), preserving the pre-JF-727 fetch order so the
        // per-type item feeds, and therefore the payload bytes the JF-717
        // equivalence classes hash, are unchanged. The count lands through
        // the row's StoreCount element; the emptiness pre-check reads the
        // collection through AllTypeLegsEmpty (JF-737: the named, doubly
        // pinned predicate over the full table; the pin contract lives on
        // the predicate), so a fourth type's row cannot be silently
        // excluded from it: the pre-JF-727 missed CONJUNCTION term was
        // behavioral (a library holding only the missed type's items took
        // the emptiness skip and never synced), the missed count only
        // cosmetic. The Items placeholder is unconditionally replaced for
        // every row before anything reads it.
        for (int i = 0; i < typeLegs.Length; i++)
        {
            ref var leg = ref typeLegs[i];
            leg.Items = leg.FetchItems();
            leg.StoreCount(leg.Items.Count);

            // JF-946: the song leg's honest cut signal. The cap bound the
            // DEDUPED distinct count (play-count ordered), so a cut means the
            // least-played titles lost their catalog values, not that a fetch
            // saturated; surfaced here because only this loop sees both the
            // capped N and the pre-cap M.
            if (leg.Type == CatalogType.Song && result.SongTitleTotal > result.SongCount)
            {
                _logger.LogWarning(
                    "Song title catalog cut for user {UserId}: kept the {Kept} most-played of {Distinct} distinct titles (cap {Cap}); the least-played {Dropped} titles have no catalog value (JF-946)",
                    user.Id,
                    result.SongCount,
                    result.SongTitleTotal,
                    EffectiveMaxCatalogValues,
                    result.SongTitleTotal - result.SongCount);
            }
        }

        if (AllTypeLegsEmpty(typeLegs))
        {
            _logger.LogWarning("No artists, albums, series, audiobooks, movies or songs found for user {UserId}, skipping sync", user.Id);
            // JF-949: the early return bypasses the tail verdict, so a failed
            // teardown still fails the run here (see the tail clause).
            if (songTeardownFailed)
            {
                result.Success = false;
            }

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
            async Task<(string? Version, long PayloadBytes)> SyncTypeLegAsync(
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
                    return (null, 0);
                }
            }

            // Create/update catalogs with locale-specific phonetic synonyms.
            // Each entry pairs the minted version with the catalog id as it
            // stands right after the leg; types that minted no version (frozen
            // or zero items) are absent, which is the JF-495
            // null-id-with-null-version rule the injection reads below. A
            // byte-identical-payload skip contributes the class's shared mint
            // (JF-717), a version this run minted for this same catalog.
            var minted = new Dictionary<CatalogType, (string? Version, string? CatalogId, long PayloadBytes)>();
            foreach (var leg in typeLegs)
            {
                (string? version, long payloadBytes) = await SyncTypeLegAsync(
                    leg.Type, leg.Items, leg.StoredCatalogId(), leg.StoreCatalogId, leg.Name, leg.Description).ConfigureAwait(false);
                if (version != null)
                {
                    minted[leg.Type] = (version, leg.StoredCatalogId(), payloadBytes);
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
                (string? Version, string? CatalogId, long PayloadBytes) Minted(CatalogType type) => minted.GetValueOrDefault(type);

                // JF-951: the pre-flight size guard's payload feed. The bytes of
                // every type that minted a version this run ride the leg; the
                // guard inside CatalogManager sums only the types actually wired
                // (a non-null catalog id). Frozen and zero-item types are absent
                // and contribute nothing, matching what the injection forwards.
                var payloadBytesByType = minted.ToDictionary(static kv => kv.Key, static kv => kv.Value.PayloadBytes);

                var modelUpdate = await _catalogManager.UpdateInteractionModelAsync(
                    user.SmapiDeviceToken.AccessToken,
                    skillId,
                    DevelopmentStage,
                    locale,
                    Minted(CatalogType.Artist).CatalogId,
                    Minted(CatalogType.Album).CatalogId,
                    Minted(CatalogType.Series).CatalogId,
                    Minted(CatalogType.Audiobook).CatalogId,
                    Minted(CatalogType.Movie).CatalogId,
                    Minted(CatalogType.Artist).Version,
                    Minted(CatalogType.Album).Version,
                    Minted(CatalogType.Series).Version,
                    Minted(CatalogType.Audiobook).Version,
                    Minted(CatalogType.Movie).Version,
                    Minted(CatalogType.Song).CatalogId,
                    songCatalogVersion: Minted(CatalogType.Song).Version,
                    payloadBytesByType: payloadBytesByType,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // JF-951: a pre-flight size-guard skip keeps the locale's previous
                // good model, so the leg still counts as completed (no PUT failed).
                // The skip is an EXPECTED user-configuration outcome: ONE warning
                // per skipped locale carries the numbers (from the guard's result)
                // and the levers, the locale lands in the SyncResult, and the
                // ledger row records it WITHOUT clobbering a settled status (see
                // RecordOversizeSkipInLedger). Every other outcome takes the
                // normal PUT path.
                if (modelUpdate.BuildStatus == CatalogManager.BuildStatusSkippedOversize)
                {
                    result.RecordSkippedOversizeLocale(locale);
                    RecordOversizeSkipInLedger(locale, modelUpdate);
                    _logger.LogWarning(
                        "Catalog sync locale {Locale} SKIPPED its model PUT for user {UserId} (JF-951 pre-flight size guard): {Reason}. Amazon's per-model cap is {CapBytes} bytes; the guard compares against the {ThresholdBytes}-byte safety threshold (a {Margin}-byte margin over the estimator's gaps), so a locale under the threshold clears Amazon's cap with headroom. The previous good model stays live and the catalogs themselves uploaded fine. {Levers}",
                        locale,
                        user.Id,
                        modelUpdate.CanaryError,
                        CatalogManager.ModelSizeCapBytes,
                        CatalogManager.ModelSizeSafetyThresholdBytes,
                        CatalogManager.ModelSizeCapBytes - CatalogManager.ModelSizeSafetyThresholdBytes,
                        CatalogManager.OversizeGuardLeversText);
                }
                else
                {
                    // JF-495: catalog-sync model PUTs must appear in the per-locale
                    // status ledger, not just ModelDeploymentManager deployments.
                    // JF-705: the leg's frozen types ride the entry too.
                    RecordModelUpdateInLedger(locale, modelUpdate, frozenTypes);
                }
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

        // JF-949: a failed teardown must not read as a clean run, or
        // CatalogSyncTask stamps LastCatalogSync and the retry waits out the
        // full sync interval with the tracklist still uploaded. Placement AFTER
        // the frozen-type verdict: an independent failure clause, so a teardown
        // failure alone fails the run exactly like a freeze does.
        if (songTeardownFailed)
        {
            result.Success = false;
        }

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
        string skippedClause = result.SkippedOversizeLocales.Count > 0
            ? $" (JF-951 size-guard skips: {string.Join(", ", result.SkippedOversizeLocales)}; previous good models kept)"
            : string.Empty;
        _logger.LogInformation(
            "Catalog sync completed for user {UserId}: {Succeeded}/{Total} locales, {Artists} artists, {Albums} albums, {Series} series, {Audiobooks} audiobooks, {Movies} movies, {Songs}/{SongTotal} song titles, {ElapsedMs}ms total{FrozenClause}",
            user.Id, localesSucceeded, locales.Count, result.ArtistCount, result.AlbumCount, result.SeriesCount, result.AudiobookCount, result.MovieCount, result.SongCount, result.SongTitleTotal, totalSw.ElapsedMilliseconds, frozenClause + skippedClause);

        return result;
    }

    /// <summary>
    /// JF-949 (JF-946 review F1): the song-catalog opt-out teardown. Ordering is
    /// load-bearing: (1) strip the SongTitle catalog wiring from EVERY
    /// catalog-wiring-supported locale (the whole embedded-model roster, NOT the
    /// run's CatalogSyncLocales-filtered list: the wiring was applied to whatever
    /// roster the opt-in-time sync carried, so a roster narrowed since then must
    /// not leave locale B wired while the catalog under it is deleted, JF-949
    /// review round), restoring each locale's OWN embedded static block;
    /// (2) delete the SMAPI catalog, but ONLY when every strip succeeded, so no
    /// model is ever left referencing a deleted catalog; (3) clear
    /// <see cref="Entities.User.SongCatalogId"/> ONLY after the delete, so a
    /// failure anywhere keeps the id and the next sync run retries the whole
    /// teardown. Idempotent end to end: an already-static or absent SongTitle
    /// block strips nothing (CatalogManager.StripCatalogWiringAsync returns
    /// "Skipped" without a PUT), and a 404 delete counts as done. Each strip
    /// outcome rides the per-locale status ledger via
    /// <see cref="RecordModelUpdateInLedger"/> like any other catalog-sync PUT.
    /// The user record mutation persists through the caller's save
    /// (CatalogSyncTask / SkillStartup persist after the run); an extra guarded
    /// save here makes the clear durable even on a path that returns early.
    /// </summary>
    /// <param name="user">The plugin user whose song catalog to tear down.</param>
    /// <param name="skillId">The user's skill ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the teardown completed (or found nothing left to do); false when a step failed and the id must stay for retry.</returns>
    private async Task<bool> TearDownSongCatalogAsync(
        Entities.User user,
        string skillId,
        CancellationToken cancellationToken)
    {
        // The strip roster is the FULL catalog-wiring-supported locale set (every
        // embedded model, JF-543-filtered), independent of CatalogSyncLocales: the
        // wiring exists wherever a PAST sync put it, and a narrowed current roster
        // must not blind the strip to a locale it no longer syncs (JF-949 review
        // round finding 1).
        List<string> locales = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .Select(m => m.Item1)
            .Where(CatalogManager.IsCatalogWiringSupported)
            .ToList();

        string? catalogId = user.SongCatalogId;
        if (string.IsNullOrEmpty(catalogId))
        {
            // Nothing stored: nothing to tear down (the method's idempotent entry).
            return true;
        }

        if (user.SmapiDeviceToken == null)
        {
            // Unreachable through SyncUserLibraryAsync (its entry guard returns on a
            // null token first), but a defensive landing here must NOT read as a
            // completed teardown: returning true with the id kept would orphan the
            // Amazon-side tracklist silently. Report failure so the run stays
            // unclean and the next sync retries (JF-949 code-review round).
            _logger.LogWarning(
                "Song catalog teardown for user {UserId}: no SMAPI device token; the teardown cannot run and the stored catalog id stays (JF-949)",
                user.Id);
            return false;
        }

        string accessToken = user.SmapiDeviceToken.AccessToken;
        string slotTypeName = CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Song];

        _logger.LogInformation(
            "Song catalog opt-out teardown for user {UserId}: stripping SongTitle wiring from {LocaleCount} locales, then deleting catalog {CatalogId} (JF-949)",
            user.Id, locales.Count, catalogId);

        var failedLocales = new List<string>();
        for (int i = 0; i < locales.Count; i++)
        {
            string locale = locales[i];
            if (i > 0)
            {
                // The same SMAPI rate-limit spacing the per-locale legs use;
                // the test seam shares the legs' override.
                await Task.Delay(InterLocaleDelayMsForTest ?? InterLocaleDelayMs, cancellationToken).ConfigureAwait(false);
            }

            // The embedded static block is OPTIONAL: only it-IT/de-DE ship a
            // SongTitle block, and only locales where the live model actually
            // carries a WIRED block need a replacement (a wired block in a locale
            // with no embedded block is removed outright, the JF-952 removal
            // shape). A null block is NOT a failure: the strip itself decides
            // from the live model (JF-949 code-review round).
            string? embeddedBlock = CatalogSeedEnrichment.GetEmbeddedTypeBlock(locale, slotTypeName);

            try
            {
                CatalogModelUpdateResult update = await _catalogManager.StripCatalogWiringAsync(
                    accessToken, skillId, DevelopmentStage, locale, slotTypeName, embeddedBlock, cancellationToken).ConfigureAwait(false);

                // Only a PUT that actually happened rides the per-locale status
                // ledger; a "Skipped" strip leaves the locale's previous row
                // untouched (overwriting a Succeeded row with Skipped would make
                // the admin panel's ModelsDeployed checklist false-negative,
                // JF-949 code-review round).
                if (update.BuildStatus != "Skipped")
                {
                    RecordModelUpdateInLedger(locale, update, new List<CatalogType>());
                }

                if (update.BuildStatus is not ("SUCCEEDED" or "Skipped"))
                {
                    _logger.LogError(
                        "Song catalog teardown for {Locale}: the wiring strip did not settle ({BuildStatus}); the locale keeps its wired block and the catalog stays (JF-949)",
                        locale, update.BuildStatus);
                    failedLocales.Add(locale);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Song catalog teardown for {Locale}: the wiring strip failed; the locale keeps its wired block and the catalog stays (JF-949)",
                    locale);
                failedLocales.Add(locale);
            }
        }

        if (failedLocales.Count > 0)
        {
            _logger.LogError(
                "Song catalog teardown for user {UserId} stopped before the catalog delete: {Count} locale strip(s) failed ({Locales}); the catalog and the stored id stay so the next sync retries (JF-949)",
                user.Id, failedLocales.Count, string.Join(", ", failedLocales));
            return false;
        }

        try
        {
            await _catalogManager.DeleteCatalogAsync(accessToken, catalogId!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Song catalog teardown for user {UserId}: the SMAPI catalog delete failed for {CatalogId}; the stored id stays so the next sync retries (JF-949)",
                user.Id, catalogId);
            return false;
        }

        // The wiring strips succeeded and the catalog is gone: the stored id's
        // catalog no longer exists, so keeping it would poison a later opt-in
        // re-sync (SyncCatalogForLocaleAsync would update a dead catalog).
        user.SongCatalogId = null;
        try
        {
            Plugin.Instance?.Configuration.PersistUnderLedgerLock();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist the SongCatalogId clear for user {UserId} (non-fatal; the caller's own save covers it) (JF-949)",
                user.Id);
        }

        _logger.LogInformation(
            "Song catalog teardown completed for user {UserId}: wiring stripped, catalog {CatalogId} deleted, SongCatalogId cleared (JF-949)",
            user.Id, catalogId);
        return true;
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
        WriteLedgerEntry(locale, _ => new Configuration.LocaleModelStatus
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
    /// Records a JF-951 pre-flight size-guard SKIP in the per-locale status
    /// ledger WITHOUT clobbering the settled status: the guard sent no PUT and
    /// the locale's live model is UNCHANGED, so a SUCCEEDED row from a previous
    /// run stays SUCCEEDED (DiagnosticsController.ModelsDeployed and the admin
    /// panel's ModelsDeployed checklist match only Succeeded, and
    /// <see cref="RecordModelUpdateInLedger"/>'s own doc rejected a distinct
    /// failing status for exactly this reason). What changes is the CATALOG
    /// state, which is durable: the row's caveat carries the new
    /// OversizeSkipped bit and the Error carries the guard's numbers (the
    /// effective size, the threshold, and the two levers), so the config page's
    /// locale-status dump tells the user their chosen combination is
    /// incompatible. The write REPLACES the previous row wholesale (the skip
    /// text is this writer's own diagnostic; a previous foreign Error is
    /// superseded by it, and a previous FrozenCatalogs caveat describes a
    /// freeze the skip-run's own frozenTypes did not add; the next real PUT
    /// write or startup capture rewrites the row either way). A later run that
    /// wires the locale again overwrites the row with a plain SUCCEEDED row and
    /// the caveat clears.
    /// </summary>
    private void RecordOversizeSkipInLedger(string locale, CatalogModelUpdateResult modelUpdate)
    {
        WriteLedgerEntry(locale, previous => new Configuration.LocaleModelStatus
        {
            // The settled-status clamp (the no-PUT writer's rule): a previous
            // SUCCEEDED stays SUCCEEDED (the good model IS still live), FAILED/
            // TIMEOUT keep their diagnostic weight, anything transient clamps
            // to the healthy-neutral "Skipped".
            Status = PreservedOrSkippedStatus(previous?.Status),
            LastUpdated = DateTime.UtcNow,
            Error = modelUpdate.CanaryError,
            Caveat = Configuration.CatalogLedgerCaveats.OversizeSkipped,
            Source = CatalogSyncLedgerSource
        }, "JF-951 size-guard skip");
    }

    /// <summary>
    /// The ledger-write shell (guard, atomic update, save, non-fatal catch)
    /// shared by both catalog-sync ledger writers (the JF-705 PUT path above
    /// and the JF-709 no-PUT path); the writers differ only in how the entry's
    /// fields are derived, and the compose callback is handed the CURRENT row
    /// under the configuration's ledger lock (JF-724), so the no-PUT writer's
    /// read-decide-carry runs atomically (the PUT path ignores the current row
    /// but still writes through the one locked shell, so the guard/save/catch
    /// plumbing stays single-sited). The startup capture (JF-710) is the third
    /// ledger writer and keeps its own write under a whole-capture catch, now
    /// with per-locale isolation inside it (JF-722) and routed through the same
    /// atomic UpdateLocaleModelStatus (JF-724); the capture's JF-722 deferred
    /// refresh is the fourth writer, same family (it only rewrites
    /// capture-authored rows), same atomic path.
    /// The what label names the failed write in the non-fatal log line so triage
    /// reads the right surface.
    /// </summary>
    private void WriteLedgerEntry(
        string locale,
        Func<Configuration.LocaleModelStatus?, Configuration.LocaleModelStatus?> compose,
        string what)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            config.UpdateLocaleModelStatus(locale, compose);
            // The ONE locked save path (JF-724): SaveConfiguration serializes
            // the live collection, which would otherwise race another writer's
            // Add/replace mid-enumeration.
            config.PersistUnderLedgerLock();
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
    /// row's FREE-TEXT diagnostic, carried ONLY when the previous row was not
    /// authored by this subsystem's own shapes per
    /// <see cref="IsOwnShapeLedgerCaveat"/>, MIGRATED if it is still a
    /// pre-JF-721 composed text (<see cref="MigrateLegacyLedgerError"/>, so the
    /// stale clause/tail never bakes onto this caveat-carrying row) and
    /// ATTRIBUTED with <see cref="CarriedDiagnosticLedgerPrefix"/> so it cannot
    /// read as the current run's error. A carried diagnostic survives exactly
    /// ONE all-frozen run, because this write's own NoCatalogPut bit makes the
    /// NEXT all-frozen run replace the row wholesale instead of re-carrying it
    /// (the JF-709 rework F1 no-nesting rule, field-keyed since JF-721). Source
    /// is always this writer's own label: catalog sync authors THIS row.
    /// </summary>
    private void RecordNoPutFrozenLegInLedger(string locale, List<CatalogType> frozenTypes)
    {
        var (frozenCaveat, frozenCatalogTypes) = FrozenCatalogCaveat(frozenTypes);

        // The previous-row read-decide-carry runs INSIDE the locked compose
        // (JF-724): it was the last unguarded read-modify-write of this writer
        // (the startup capture or its deferred refresh landing between the old
        // separate Get and the Set was overwritten until the next run); the
        // read and the write now share one ledger-lock acquisition, and the
        // non-fatal guard around the whole update is WriteLedgerEntry's.
        WriteLedgerEntry(locale, previous =>
        {
            string? carriedError = null;
            if (previous?.Error is { Length: > 0 } previousError
                && !IsOwnShapeLedgerCaveat(previous.Caveat))
            {
                // Gate-marker rework F1: a pre-JF-721 composed previous Error
                // migrates once here too; this carry is the path that would
                // otherwise bake the stale clause/tail text onto a caveat-carrying
                // row (the clause this run writes its own fields for). Gate-marker
                // rework F3: the carried diagnostic is ATTRIBUTED in the free text
                // so the admin can tell a prior-run failure from the current run's
                // state; the prefix is display-only, nothing parses it.
                carriedError = MigrateLegacyLedgerError(previousError).Error is { Length: > 0 } foreign
                    ? CarriedDiagnosticLedgerPrefix + foreign
                    : null;
            }

            return new Configuration.LocaleModelStatus
            {
                Status = PreservedOrSkippedStatus(previous?.Status),
                LastUpdated = DateTime.UtcNow,
                Error = carriedError,
                Caveat = frozenCaveat | Configuration.CatalogLedgerCaveats.NoCatalogPut,
                FrozenCatalogTypes = frozenCatalogTypes,
                Source = CatalogSyncLedgerSource
            };
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
    /// The display attribution the no-PUT writer prefixes onto the previous
    /// row's diagnostic it carries (gate-marker rework F3): without it a stale
    /// prior-run failure renders fully visible beside "no PUT this run" and
    /// reads as the current run's error. DISPLAY-ONLY, unlike the deleted
    /// PreviousLedgerDiagnosticPrefix: nothing recognizes or strips it, and it
    /// survives later captures verbatim because it stays true (the carried text
    /// IS from a previous run). Deliberately not shaped like any legacy
    /// migration literal, so <see cref="MigrateLegacyLedgerError"/> passes it
    /// through untouched.
    /// </summary>
    internal const string CarriedDiagnosticLedgerPrefix = "previous run: ";

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
    /// UPGRADE TRANSITION (JF-721 design, hardened by the gate-marker rework
    /// F1): a persisted row whose Error still carries a pre-JF-721 COMPOSED
    /// text is MIGRATED once by <see cref="MigrateLegacyLedgerError"/> (the
    /// clause becomes the caveat bits + names payload, the run-scoped tail and
    /// framing drop, the bare foreign diagnostic survives) so a skip-gated
    /// capture chain can never keep displaying "no PUT this run" for a run that
    /// DID push, nor duplicate the clause beside the caveat span; the migration
    /// runs on every text-carrying path (this preserve, the no-PUT writer's
    /// carry, the refresh's clean settle). A pre-JF-721 CAPTURE-preserved row
    /// (Source "Embedded", clause-led Error, no bits) still clears on the first
    /// post-upgrade clean capture (recognition finds nothing), and a pre-JF-721
    /// own-errors row clears the same way. All shapes self-heal at the next
    /// catalog sync; steady state (every persisted row rewritten once by a
    /// field-era writer) leaves the migration a no-op passthrough.
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

        // The shared text-carrying copy (rework R2): observation-era errors
        // drop, a legacy composed Error migrates once, and the payload rides
        // only when its bit survives (the FrozenCatalogCaveat factory's pair
        // rule, enforced on every copy path; code-review F2).
        var (caveat, frozenCatalogTypes, error) = CarryLedgerCaveatAcrossCleanObservation(
            existing.Caveat, existing.FrozenCatalogTypes, existing.Error);

        // The preserve's ONE deliberate divergence from the refresh (named so
        // the asymmetry cannot read as drift): the run-scoped no-PUT bit never
        // survives a capture, because a capture follows a skill UPDATE that
        // pushed a new version. The refresh does not mask it (its family rows
        // never carry the bit today; it carries family bits verbatim).
        return new PreservedLedgerCaveat(
            caveat & ~Configuration.CatalogLedgerCaveats.NoCatalogPut,
            frozenCatalogTypes,
            error);
    }

    /// <summary>
    /// The ONE owner of the text-carrying copy shape shared by the startup
    /// capture's preserve and the deferred refresh's clean-settle arm (JF-721
    /// gate-marker rework R2; the DropObservedBuildErrors one-owner precedent):
    /// drop the observation-era arm with its Error, migrate a pre-JF-721
    /// composed Error once (minting the frozen caveat from a clause the text
    /// carried), and null the payload whenever the FrozenCatalogs bit does not
    /// survive. The two callers differ ONLY in the preserve's run-scoped
    /// NoCatalogPut mask, applied at its call site and named there; any future
    /// rule of this copy shape lands here once for both writers.
    /// </summary>
    /// <param name="caveat">The source row's caveat bits.</param>
    /// <param name="frozenCatalogTypes">The source row's names payload.</param>
    /// <param name="error">The source row's free-text Error.</param>
    /// <returns>The carried (caveat, payload, error) triple.</returns>
    internal static (Configuration.CatalogLedgerCaveats Caveat, string? FrozenCatalogTypes, string? Error) CarryLedgerCaveatAcrossCleanObservation(
        Configuration.CatalogLedgerCaveats caveat,
        string? frozenCatalogTypes,
        string? error)
    {
        var (carriedCaveat, carriedError) = DropObservedBuildErrors(caveat, error);
        var (migratedError, migratedTypes) = MigrateLegacyLedgerError(carriedError);
        if (migratedTypes != null)
        {
            carriedCaveat |= Configuration.CatalogLedgerCaveats.FrozenCatalogs;
        }

        return (
            carriedCaveat,
            carriedCaveat.HasFlag(Configuration.CatalogLedgerCaveats.FrozenCatalogs)
                ? frozenCatalogTypes ?? migratedTypes
                : null,
            migratedError);
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

    /// <summary>The frozen-clause literal of the pre-JF-721 COMPOSED Error text
    /// (the deleted FrozenLedgerClauseMarker). LEGACY MIGRATION vocabulary only:
    /// no field-era writer emits it and nothing recognizes it as protocol; it
    /// exists so <see cref="MigrateLegacyLedgerError"/> can date a persisted
    /// row's text as pre-JF-721 and decompose it once.</summary>
    private const string LegacyFrozenClauseText = " FROZEN (last-good pinned)";

    /// <summary>The no-PUT tail literal of the pre-JF-721 composed Error text
    /// (the deleted NoPutLedgerTail). Legacy migration vocabulary only.</summary>
    private const string LegacyNoPutTailText = "; no PUT this run";

    /// <summary>The trailed-foreign framing literal of the pre-JF-721 composed
    /// Error text (the deleted PreviousLedgerDiagnosticPrefix). Legacy migration
    /// vocabulary only.</summary>
    private const string LegacyPreviousFramingText = "; previous: ";

    /// <summary>The observed-errors prefix literal of the pre-JF-721 composed
    /// Error text (the deleted ObservedBuildErrorsLedgerPrefix). Legacy
    /// migration vocabulary only.</summary>
    private const string LegacyObservedBuildErrorsText = "build errors: ";

    /// <summary>
    /// The ONE-SHOT migration of a pre-JF-721 COMPOSED ledger Error (JF-721
    /// gate-marker F1). The old writers baked the frozen clause, the run-scoped
    /// no-PUT tail, the "previous: " framing, and the build-errors prefix INTO
    /// the free text; the field-era writers never emit any of them, so an Error
    /// still carrying one dates the row as pre-JF-721 and must not be copied
    /// forward verbatim (a skip-gated capture chain would keep displaying "no
    /// PUT this run" for a run that DID push, and the baked clause would
    /// duplicate beside the caveat span once the fields carry it). The
    /// migration decomposes exactly the old composition grammar ONE way, into
    /// fields: the frozen clause (when present) becomes the returned names
    /// payload for the caller to OR into its caveat bits, the run-scoped tail
    /// and framing drop, and what survives is the bare foreign diagnostic (or
    /// null when nothing does). A pre-upgrade observed-errors text (the prefix
    /// shape) is superseded observation-era text and clears entirely. A
    /// field-era Error (no legacy literal anywhere) passes through UNTOUCHED,
    /// which is the steady state after every persisted row has been rewritten
    /// once; this helper is migration scaffolding, NOT a wire protocol.
    /// RESIDUAL, HARDENED (code-review refresh R1): a field-era diagnostic that
    /// incidentally CARRIES one of the literals is misdated and decomposed.
    /// The clause arm guards against exactly that: the marker only counts as a
    /// clause when the text before it is clause-SHAPED (non-empty, no ';' and
    /// no ':', ending in the catalog noun, exactly what every legacy
    /// composition's leading "names catalogs" head looks like), so a marker
    /// quoted mid-sentence in a foreign diagnostic does NOT mint a bogus
    /// FrozenCatalogs bit and the text passes through untouched. The remaining
    /// window is a foreign text carrying the bare tail/framing literals or a
    /// clause-shaped prefix, shapes the fixed canary format and SMAPI error
    /// messages do not produce; a misdated row heals at the next sync rewrite.
    /// VIGILANCE (the old family's burden, one last time): no writer may ever
    /// emit these literals into an Error, which is why
    /// <see cref="CarriedDiagnosticLedgerPrefix"/> is deliberately shaped to
    /// match none of them.
    /// </summary>
    /// <param name="error">The Error text a writer is about to carry forward.</param>
    /// <returns>The migrated free text (null when nothing survives) and the
    /// frozen-type names the text carried (null when it carried no clause; the
    /// caller ORs the FrozenCatalogs bit when non-null).</returns>
    internal static (string? Error, string? MigratedFrozenCatalogTypes) MigrateLegacyLedgerError(string? error)
    {
        if (error is not { Length: > 0 })
        {
            return (error, null);
        }

        // The old own-errors shape (bare or trailed by the no-PUT writer):
        // superseded observation-era text, clears like the field-era arm rows.
        if (error.StartsWith(LegacyObservedBuildErrorsText, StringComparison.Ordinal))
        {
            return (null, null);
        }

        // The clause arm (R1 hardening): a marker counts as the legacy clause
        // only when its head is clause-shaped, so a marker quoted inside a
        // foreign diagnostic neither mints the frozen bit nor chops the text.
        int markerAt = error.IndexOf(LegacyFrozenClauseText, StringComparison.Ordinal);
        bool clauseLed = false;
        string head = string.Empty;
        if (markerAt >= 0)
        {
            head = error[..markerAt];
            clauseLed = head.Length > 0
                && !head.Contains(';')
                && !head.Contains(':')
                && (head.EndsWith(" catalogs", StringComparison.Ordinal) || head.EndsWith(" catalog", StringComparison.Ordinal));
        }

        if (!clauseLed
            && !error.Contains(LegacyNoPutTailText, StringComparison.Ordinal)
            && !error.Contains(LegacyPreviousFramingText, StringComparison.Ordinal))
        {
            // Field-era free text (or a foreign text quoting a literal in a
            // non-legacy shape): untouched.
            return (error, null);
        }

        string migrated = error
            .Replace(LegacyNoPutTailText, string.Empty, StringComparison.Ordinal)
            .Replace(LegacyPreviousFramingText, "; ", StringComparison.Ordinal);

        // The clause LEADS every legacy composition, so chopping through the
        // marker's end removes the names + noun + marker in one cut; the names
        // for the payload are the head minus its trailing catalog noun.
        string? migratedTypes = null;
        if (clauseLed)
        {
            migrated = migrated[(markerAt + LegacyFrozenClauseText.Length)..];
            foreach (string noun in new[] { " catalogs", " catalog" })
            {
                if (head.EndsWith(noun, StringComparison.Ordinal))
                {
                    head = head[..^noun.Length];
                    break;
                }
            }

            migratedTypes = head.Length == 0 ? null : head.Replace(" + ", ",");
        }

        migrated = migrated.Trim(' ', ';', ',');
        return (migrated.Length == 0 ? null : migrated, migratedTypes);
    }

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
    /// JF-954.8: a thin pass-through to the shared query core
    /// <see cref="FetchCatalogItems"/> at the sync's cap; the structural pin on
    /// this method (exactly the wiring-table lambdas call it) stays on this
    /// wrapper, while the query shape itself is single-sourced so the snapshot
    /// export's fetches cannot drift on scope, filter, ordering or limit.
    /// </summary>
    private IReadOnlyList<BaseItem> FetchLibraryItems(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        BaseItemKind itemKind)
        => FetchCatalogItems(jellyfinUser, user, itemKind, EffectiveMaxCatalogValues);

    /// <summary>
    /// JF-954.8: the ONE catalog item query both fetch consumers share, the
    /// sync's wiring-table lambdas (through <see cref="FetchLibraryItems"/>,
    /// whose single-call-site pin stays on that wrapper) and the snapshot
    /// export (<see cref="FetchTypeItems"/>), so scope, library filter,
    /// ordering and limit cannot drift between what the sync uploads and what
    /// the export describes. STRICT scope, no items-by-name bypass
    /// (includeItemsByName: false): both feeds become (or describe) the
    /// persistent SMAPI catalog upload, so a bypass would let excluded-library
    /// artist names live on Amazon's side indefinitely, not just surface
    /// transiently in a cold-window search (JF-457). Rationale for the
    /// automatic bypass itself: LibraryFilter.ApplyItemsByNameBypass (JF-456).
    /// </summary>
    private IReadOnlyList<BaseItem> FetchCatalogItems(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        BaseItemKind itemKind,
        int limit)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true),
            Limit = limit,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManager, includeItemsByName: false);

        return _libraryManager.GetItemList(query);
    }

    /// <summary>
    /// JF-946: the song leg's dedicated source. Fetches the user's Audio
    /// items UNCAPPED (the preparation pipeline dedups on the stripped form
    /// BEFORE the cap, so a query-level Limit would bind on the pre-dedup
    /// count), reads each track's play count for the ordering, runs
    /// <see cref="SongTitlePreparation.Prepare"/>, and returns the prepared
    /// tracks as fresh item instances whose Name carries the STRIPPED title
    /// (the catalog value; mutating the query result writes nothing back).
    /// </summary>
    private List<BaseItem> FetchAndPrepareSongTitles(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SyncResult result)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            // Lean field set (JF-946 review): only Id/Name/DateCreated are
            // read, and the uncapped 12k+ fetch would otherwise left-join
            // provider/image collections for every row. EnableUserData stays
            // ON deliberately: the per-track GetUserData reads these attached
            // rows in memory (turning it off silently zeroes every play
            // count and degrades the ordering to recency).
            DtoOptions = new DtoOptions(false) { EnableImages = false },
            // JF-946 review: a bound, not the sibling cap. The dedup-before-cap
            // design needs the RAW tracks, but a public user's 300k-track
            // library must not make plugin memory proportional to raw track
            // count. A 4x multiple of the value cap covers every realistic
            // suffix/duplicate ratio while bounding the fetch; the cut
            // warning below fires when the distinct count still exceeds the
            // cap.
            Limit = EffectiveMaxCatalogValues * 4
        };

        // Same strict-scope rationale as FetchLibraryItems (JF-457).
        LibraryFilter.ApplyLibraryFilter(query, user, _libraryManager, includeItemsByName: false);

        IReadOnlyList<BaseItem> tracks = _libraryManager.GetItemList(query);

        var candidates = new List<(BaseItem Item, string Name, int PlayCount, DateTime DateCreated)>(tracks.Count);
        foreach (BaseItem track in tracks)
        {
            int playCount = _userDataManager.GetUserData(jellyfinUser, track)?.PlayCount ?? 0;
            candidates.Add((track, track.Name ?? string.Empty, playCount, track.DateCreated));
        }

        List<(BaseItem Item, string Name)> prepared = SongTitlePreparation.Prepare(candidates, EffectiveMaxCatalogValues, out int totalDistinct);

        // The distinct pre-cap count (the M of "N of M synced") lands here:
        // the table's StoreCount channel carries only the capped N.
        result.SongTitleTotal = totalDistinct;

        var songItems = new List<BaseItem>(prepared.Count);
        foreach ((BaseItem item, string name) in prepared)
        {
            item.Name = name;
            songItems.Add(item);
        }

        return songItems;
    }

    /// <summary>
    /// Sync a catalog for a specific locale: build locale-specific payload with phonetic synonyms,
    /// upload to SMAPI, and return the minted version. Creates the catalog when no id is
    /// stored yet, persisting the new id through <paramref name="storeCatalogId"/> (the
    /// per-type wiring table's paired setter, JF-711). Returns a null version with
    /// zero payload bytes when the type has no items this run. A payload byte-identical
    /// to one already uploaded this run skips the upload and returns the version that
    /// earlier leg minted (JF-717), so the caller wires the equivalence class's shared
    /// (catalogId, version) pair into this locale's model. JF-951: the pair also
    /// carries the payload's serialized byte length, the feed for the pre-flight
    /// effective-size guard (always computed: the JF-717 skip path reports the same
    /// bytes as the leg that minted the shared version).
    /// </summary>
    private async Task<(string? Version, long PayloadBytes)> SyncCatalogForLocaleAsync(
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
            return (null, 0);
        }

        // JF-825 (code-review F2): the pre-fix >= warning was the ONLY signal
        // that the fetch itself had silently capped a huge library, and the
        // honest drop warning below cannot fire for that state (nothing is
        // dropped when the payload lands exactly at the cap, e.g. a seedless
        // type like Series), so the saturation signal stands on its own.
        // Worded as "reached", never "exceeded": a library of exactly cap
        // items reads identically to a capped larger one.
        // JF-946 review: the SONG leg's cut is NOT here. Its query bound is a
        // 4x multiple (dedup-before-cap) and its honest cut signal lives in
        // the sourcing loop, where the pre-cap distinct count is known; this
        // fetch-cap wording would misdirect triage toward raising a limit
        // that was never the binder.
        if (catalogType != CatalogType.Song && items.Count >= EffectiveMaxCatalogValues)
        {
            _logger.LogWarning(
                "Catalog {Type} fetch reached the {Limit}-value cap for user {UserId}; library items beyond the cap in sort order were not fetched",
                catalogType,
                EffectiveMaxCatalogValues,
                user.Id);
        }

        // JF-954.8: the ONE final-payload build (the same-name-deduped item
        // tuples, the type's synonym generator, FromItems, the seed merge and
        // the cap cut), shared verbatim with the snapshot export through
        // BuildFinalCatalogPayload so the two consumers cannot diverge on the
        // values, synonyms, truncation or cap the upload carries.
        FinalCatalogPayload built = BuildFinalCatalogPayload(user, catalogType, items, locale);
        CatalogPayload payload = built.Payload;

        if (built.Dropped.Count > 0)
        {
            // The warning fires exactly because entries were dropped and names
            // them (the debug-logging policy): the composition split comes
            // from the cut itself (TruncateTo owns which block each dropped
            // entry came from), and the sample bounds the line length when a
            // huge library drops a huge tail. The reserve-on-any-cut policy
            // puts the whole drop on the library leg, so a nonzero static leg
            // is the canary for the reserved block alone exceeding the cap
            // (test seams or a pathological seed block).
            _logger.LogWarning(
                "Truncated {Type} catalog to {Limit} values for user {UserId}: dropped {DroppedCount} lowest-priority entries ({DroppedStaticCount} static seed values, {DroppedLibraryCount} library values); first dropped: {DroppedSample}",
                catalogType,
                EffectiveMaxCatalogValues,
                user.Id,
                built.Dropped.Count,
                built.DroppedStaticCount,
                built.Dropped.Count - built.DroppedStaticCount,
                string.Join(", ", built.Dropped.Take(DroppedNameSampleSize).Select(v => v.Name.Value)));
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
        // JF-951: the payload is UTF-8 encoded ONCE; the byte count feeds the
        // pre-flight size guard and the same bytes hash for the JF-717 dedup key.
        byte[] payloadUtf8 = System.Text.Encoding.UTF8.GetBytes(payloadJson);
        long payloadBytes = payloadUtf8.Length;

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
        string payloadHash = Convert.ToHexString(SHA256.HashData(payloadUtf8));
        string payloadKey = $"{catalogType}:{catalogId}:{payloadHash}";
        if (mintedVersionsByPayload.TryGetValue(payloadKey, out string? sharedVersion))
        {
            _logger.LogInformation(
                "Catalog {Type} payload for user {UserId} locale {Locale} is identical to a payload already uploaded this run; skipping the version upload and wiring the shared catalog version {Version} minted by the earlier locale leg (JF-717)",
                catalogType,
                user.Id,
                locale,
                sharedVersion);
            return (sharedVersion, payloadBytes);
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

        return (catalogVersion, payloadBytes);
    }

    /// <summary>
    /// The ONE final-payload builder for (user, catalog type, locale), JF-954.8:
    /// the exact sequence the SMAPI sync runs per type leg (the same-name-deduped
    /// item tuples, the type's synonym generator, <see cref="CatalogPayload.FromItems"/>,
    /// the <see cref="CatalogSeedEnrichment"/> seed merge, and the
    /// <see cref="CatalogPayload.TruncateTo"/> cap cut), extracted verbatim from
    /// <see cref="SyncCatalogForLocaleAsync"/> so the authoritative snapshot
    /// export (<see cref="BuildCatalogSnapshot"/>) and the real sync cannot
    /// diverge on the values, synonyms, truncation or cap the upload carries.
    /// Pure library-read plus computation: no SMAPI call, no CatalogController
    /// cache registration, no configuration write. The sync keeps its own
    /// logging around the call: the fetch-saturation warning fires BEFORE it
    /// (in the sync's leg, which is the only consumer that knows a fetch
    /// saturated) and the truncation drop warning fires after it, reading the
    /// returned record. The dedup debug line and the seed-append audit line
    /// ride inside the build, exactly where they fired pre-extraction, so the
    /// log sequence is unchanged for the sync.
    /// </summary>
    /// <param name="user">The plugin user; its song-synonym toggle feeds the song generator.</param>
    /// <param name="catalogType">The catalog type being built.</param>
    /// <param name="items">The type's fetched items (the sync's wiring-table sourcing; the export's FetchTypeItems).</param>
    /// <param name="locale">The Alexa locale for the synonym generation.</param>
    /// <returns>The final payload plus the drop record the sync's truncation warning reports.</returns>
    internal FinalCatalogPayload BuildFinalCatalogPayload(
        Entities.User user,
        CatalogType catalogType,
        IReadOnlyList<BaseItem> items,
        string locale)
    {
        // Materialized once (JF-874 simplify finding): FromItems and the dedup
        // debug line below both enumerate this, twice per leg per locale.
        var itemTuples = items
            .Select(i => (i.Id, i.Name))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToList();

        // JF-946: the song leg's synonym policy lives on SongTitlePreparation;
        // every other type passes the raw generator, byte-identical to before.
        // JF-951: the song synonyms are USER-CONTROLLED. Disabled (the default)
        // keeps the shipped values-only policy byte-identical; enabled flows
        // the user's choice into the locale's phonetic generator per leg. The
        // effective-size consequence is the CatalogManager pre-flight guard's
        // job, which reads these payloads' bytes.
        Func<string, string, List<string>> synonymGenerator = catalogType == CatalogType.Song
            ? (name, syncLocale) => SongTitlePreparation.SongSynonyms(name, syncLocale, user.CatalogSongSynonymsEnabled)
            : PhoneticSynonymGenerator.GenerateSynonyms;

        CatalogPayload payload = CatalogPayload.FromItems(catalogType, itemTuples, synonymGenerator, locale);

        // The dedup signal (debug-logging policy): the per-type count the sync
        // reports stays the FETCH count, so this is the only line that says how
        // many same-named items collapsed into existing values (two editions of
        // one book, the same title in two libraries).
        if (payload.Values.Count < itemTuples.Count)
        {
            _logger.LogDebug(
                "Catalog {Type} collapsed {DuplicateCount} same-named library items into existing values for user {UserId} (JF-825 dedup)",
                catalogType,
                itemTuples.Count - payload.Values.Count,
                user.Id);
        }

        // JF-541 phase 2: merge the committed models' static seed values into the
        // upload. The catalog supplier replaces the static type block at deploy
        // time (CatalogManager.InjectCatalogReferences), so without this the seed
        // titles absent from the library (Thriller, Queen, ...) vanish from the
        // deployed model. Library entries win on collision; no-op for seed-less types.
        int libraryValueCount = payload.Values.Count;
        // JF-946 review: the SAME gated generator covers the seed-merge path, so a
        // future Song seed arm cannot bypass the qualifying-word gate.
        CatalogSeedEnrichment.MergeInto(payload, catalogType, synonymGenerator, locale, _logger);

        // JF-825: the cap must bind the FINAL payload (library values + the
        // appended static tail, after the FromItems same-name dedup), not
        // just the fetch: the pre-fix code fetched with Limit=cap but
        // appended the seeds AFTER the bounded fetch, so the payload sailed
        // past the cap while the log asserted a truncation that never
        // happened. The cut itself is CatalogPayload.TruncateTo's
        // reserve-on-any-cut partition (JF-848/JF-879; that policy's
        // canonical rationale lives there). The trade-off: a capped library
        // sees fewer of its own values (up to staticAppendedCount of them).
        int staticAppendedCount = payload.Values.Count - libraryValueCount;
        var (droppedValues, droppedStaticCount) = payload.TruncateTo(EffectiveMaxCatalogValues, staticAppendedCount);

        return new FinalCatalogPayload(payload, droppedValues, droppedStaticCount);
    }

    /// <summary>
    /// JF-954.8: the authoritative catalog snapshot for one user, the per-locale
    /// payloads the sync would upload, without uploading anything. Locale
    /// resolution rides <see cref="ResolveSnapshotLocales"/> (the sync's parse,
    /// no SMAPI call, the ar-SA exclusion applied), narrowed to the optional
    /// <paramref name="localeFilter"/> when one was asked; a requested locale the
    /// sync would not process (ar-SA, an unlisted locale under an explicit
    /// roster) contributes nothing. The types ride the SAME roster the sync
    /// uploads, <see cref="CatalogSlotTypes.CatalogSlotTypeNames"/>' keys (the
    /// JF-711 one-row contract keeps that key set in lockstep with the sync's
    /// wiring-table rows, and the dictionary's insertion order IS the table's
    /// row order, i.e. the upload order); the per-user song gate is the SAME
    /// predicate the sync's reduced-table filter applies
    /// (<see cref="IsCatalogTypeSyncedForUser"/>). Items fetch
    /// ONCE per type through the shared fetch (<see cref="FetchTypeItems"/>),
    /// and a zero-item type contributes nothing exactly because the sync mints
    /// nothing for one (SyncCatalogForLocaleAsync's early return). An empty
    /// locale roster (a filter matching nothing) ends the export BEFORE the
    /// type sweep, paying no fetch at all. Every
    /// (type, locale) payload comes from <see cref="BuildFinalCatalogPayload"/>,
    /// the shared builder, so this export and the next real upload cannot
    /// drift. NO SMAPI call, no catalog version, no CatalogController cache
    /// registration, no configuration write: a pure library read plus
    /// computation. Cancellation is honored at the roster, per-type and
    /// per-locale boundaries through <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user whose library scope feeds the fetches.</param>
    /// <param name="user">The plugin user (song gate, song-synonym toggle, allowed libraries).</param>
    /// <param name="localeFilter">Optional single locale to restrict the export to.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The export: locale, then slot type name, then the uploaded entries in upload order.</returns>
    internal CatalogSnapshotExport BuildCatalogSnapshot(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        string? localeFilter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string syncLocalesConfig = Plugin.Instance?.Configuration?.CatalogSyncLocales ?? string.Empty;
        IReadOnlyList<string> locales = ResolveSnapshotLocales(syncLocalesConfig);
        if (!string.IsNullOrWhiteSpace(localeFilter))
        {
            locales = locales
                .Where(l => string.Equals(l, localeFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // JF-954.8 review round: a locale roster that resolved to NOTHING (a
        // locale filter matching nothing: ar-SA, or a typo) ends the export
        // here, BEFORE the type sweep, so the discarded-everything case does
        // not pay the five capped queries and the song pipeline first.
        if (locales.Count == 0)
        {
            _logger.LogInformation(
                "Catalog snapshot export for user {UserId}: no locales to export (the resolved roster is empty); nothing fetched",
                user.Id);
            return new CatalogSnapshotExport(new Dictionary<string, Dictionary<string, List<CatalogSnapshotEntry>>>(StringComparer.Ordinal));
        }

        // The roster source rides the completion line (review round): the
        // export's "*" expands over the EMBEDDED-model roster, so its locale
        // list is what a freshly-created skill would carry; a pre-existing
        // skill whose SMAPI manifest declares fewer locales is a subset.
        _logger.LogInformation(
            "Catalog snapshot export for user {UserId}: {LocaleCount} locales ({Locales}) from the embedded-model roster (a pre-existing skill's manifest may declare fewer)",
            user.Id,
            locales.Count,
            string.Join(", ", locales));

        var table = new Dictionary<string, Dictionary<string, List<CatalogSnapshotEntry>>>(StringComparer.Ordinal);
        foreach (CatalogType catalogType in CatalogSlotTypes.CatalogSlotTypeNames.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCatalogTypeSyncedForUser(catalogType, user))
            {
                continue;
            }

            IReadOnlyList<BaseItem> items = FetchTypeItems(catalogType, jellyfinUser, user);
            if (items.Count == 0)
            {
                continue;
            }

            string slotTypeName = CatalogSlotTypes.CatalogSlotTypeNames[catalogType];
            foreach (string locale in locales)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CatalogPayload payload = BuildFinalCatalogPayload(user, catalogType, items, locale).Payload;
                var entries = payload.Values
                    .Select(v => new CatalogSnapshotEntry(v.Name.Value, v.Name.Synonyms ?? new List<string>()))
                    .ToList();
                if (entries.Count == 0)
                {
                    continue;
                }

                if (!table.TryGetValue(locale, out Dictionary<string, List<CatalogSnapshotEntry>>? types))
                {
                    types = new Dictionary<string, List<CatalogSnapshotEntry>>(StringComparer.Ordinal);
                    table[locale] = types;
                }

                types[slotTypeName] = entries;
            }
        }

        return new CatalogSnapshotExport(table);
    }

    /// <summary>
    /// JF-954.8: which locales the snapshot export covers, the sync's locale
    /// resolution WITHOUT its one SMAPI call: the same parse (<see cref="ResolveLocaleList"/>:
    /// it-IT always, the "*" roster, the explicit comma list) over a "*" roster
    /// read from the EMBEDDED interaction models instead of the SMAPI manifest,
    /// then the SAME ar-SA exclusion the sync applies
    /// (<see cref="CatalogManager.IsCatalogWiringSupported"/>). The roster is
    /// sorted Ordinal so the appended locales ride a deterministic order
    /// (it-IT still leads). STATED DIVERGENCE (review round): the sync's "*"
    /// expands over the LIVE skill's SMAPI manifest declared locales (with the
    /// it-IT fallback when that read fails), so the export's roster answers
    /// "what would the sync upload for the locales a FRESHLY-CREATED skill
    /// would carry"; a pre-existing skill whose manifest declares fewer
    /// locales has a live wiring that is a SUBSET of the snapshot's locale
    /// list. The divergence is accepted on purpose: resolving it would need
    /// the SMAPI read the export's no-SMAPI contract forbids. The roster and
    /// its source ride the export's completion log line.
    /// </summary>
    /// <param name="syncLocalesConfig">The CatalogSyncLocales configuration string.</param>
    /// <returns>The locales the export processes, ar-SA excluded.</returns>
    internal IReadOnlyList<string> ResolveSnapshotLocales(string syncLocalesConfig)
    {
        List<string> embeddedRoster = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .Select(m => m.Item1)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
        return ResolveLocaleList(syncLocalesConfig, embeddedRoster)
            .Where(CatalogManager.IsCatalogWiringSupported)
            .ToList();
    }

    /// <summary>
    /// The ONE locale-list parser behind both locale resolutions (JF-954.8):
    /// it-IT always; "*" expands over <paramref name="starRoster"/>; an
    /// explicit comma list appends its entries; everything deduped
    /// case-insensitively with the first occurrence winning. The sync passes
    /// the SMAPI manifest's locales (<see cref="GetActiveLocalesAsync"/>), the
    /// snapshot export the embedded-model roster; the ar-SA exclusion stays
    /// with the callers (the sync filters after resolving,
    /// <see cref="ResolveSnapshotLocales"/> filters through
    /// CatalogManager.IsCatalogWiringSupported), so the parser stays a pure
    /// list production exactly like the pre-JF-954.8 sync path.
    /// </summary>
    /// <param name="syncLocalesConfig">The CatalogSyncLocales configuration string; null or whitespace resolves to it-IT alone.</param>
    /// <param name="starRoster">The locales a "*" config expands to.</param>
    /// <returns>The resolved list, it-IT first.</returns>
    private static List<string> ResolveLocaleList(string? syncLocalesConfig, IEnumerable<string> starRoster)
    {
        var result = new List<string> { DefaultLocale }; // it-IT always included

        if (string.IsNullOrWhiteSpace(syncLocalesConfig))
        {
            return result;
        }

        IEnumerable<string> source = syncLocalesConfig.Trim() == "*"
            ? starRoster
            : syncLocalesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string locale in source)
        {
            if (!result.Contains(locale, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(locale);
            }
        }

        return result;
    }

    /// <summary>
    /// JF-954.8: the snapshot export's item source, ONE fetch per catalog type
    /// shared across locales (the sync's sourcing shape). The dispatch is an
    /// EXHAUSTIVE switch, not a special-case ternary, so a future type with a
    /// dedicated source (the Song precedent: play-count ordering,
    /// uncapped-then-prepared) fails LOUDLY here at the first export instead
    /// of silently riding the generic SortName fetch the sync does not use
    /// for it. Song rides <see cref="FetchSongItemsForSnapshot"/>; every
    /// other type rides the shared query through
    /// <see cref="FetchCatalogItems"/> with the kind resolved by
    /// <see cref="CatalogTypeItemKind"/>. NOTE: the sync's own fetches stay on
    /// its wiring-table lambdas (the structural pin on FetchLibraryItems binds
    /// exactly those five call sites); this dispatch is the export's only
    /// fetch path and derives every kind from the same map.
    /// </summary>
    /// <param name="catalogType">The catalog type to source.</param>
    /// <param name="jellyfinUser">The Jellyfin user whose library scope applies.</param>
    /// <param name="user">The plugin user (allowed-library filtering).</param>
    /// <returns>The type's items, in the same order the sync sources them.</returns>
    private IReadOnlyList<BaseItem> FetchTypeItems(
        CatalogType catalogType,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user)
        => catalogType switch
        {
            CatalogType.Song => FetchSongItemsForSnapshot(jellyfinUser, user),
            CatalogType.Artist or CatalogType.Album or CatalogType.Series
                or CatalogType.Audiobook or CatalogType.Movie =>
                FetchCatalogItems(jellyfinUser, user, CatalogTypeItemKind(catalogType), EffectiveMaxCatalogValues),
            _ => throw new ArgumentOutOfRangeException(nameof(catalogType), catalogType, "no catalog item source for this type"),
        };

    /// <summary>
    /// The song leg's export source (JF-954.8 review round): the SAME
    /// dedicated source the sync's wiring table uses
    /// (<see cref="FetchAndPrepareSongTitles"/>, preparation pipeline
    /// included), plus the honest cut signal the throwaway SyncResult used to
    /// swallow. The response shape is pinned to values and synonyms only, so
    /// the cut rides the plugin LOG in the sync's own wording (the JF-946
    /// warning), suffixed so triage can tell the export's line from the sync
    /// run's; the docs page names the log as the place to read the cut from.
    /// A cut means the export's SongTitle table is the kept most-played head,
    /// not the full distinct set.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user whose library scope applies.</param>
    /// <param name="user">The plugin user (allowed-library filtering).</param>
    /// <returns>The prepared, capped song items.</returns>
    private List<BaseItem> FetchSongItemsForSnapshot(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user)
    {
        var counts = new SyncResult();
        List<BaseItem> items = FetchAndPrepareSongTitles(jellyfinUser, user, counts);
        if (counts.SongTitleTotal > items.Count)
        {
            _logger.LogWarning(
                "Song title catalog cut for user {UserId}: kept the {Kept} most-played of {Distinct} distinct titles (cap {Cap}); the least-played {Dropped} titles have no catalog value (JF-946; snapshot export)",
                user.Id,
                items.Count,
                counts.SongTitleTotal,
                EffectiveMaxCatalogValues,
                counts.SongTitleTotal - items.Count);
        }

        return items;
    }

    /// <summary>
    /// The ONE per-user type-in-sync-scope gate (JF-954.8): Song rides the
    /// CatalogSongSyncEnabled opt-in (privacy: the FULL tracklist goes to
    /// Amazon), every other type is synced unconditionally. The sync's
    /// reduced-table filter and the snapshot export's roster gate both call
    /// this predicate, so the two consumers cannot disagree on which types
    /// are live for a user (the cannot-drift property this task exists to
    /// keep; a duplicated gate drifted the moment one site moved).
    /// </summary>
    /// <param name="type">The catalog type.</param>
    /// <param name="user">The plugin user whose opt-in applies.</param>
    /// <returns>True when the sync would process the type for this user.</returns>
    internal static bool IsCatalogTypeSyncedForUser(CatalogType type, Entities.User user) =>
        type != CatalogType.Song || user.CatalogSongSyncEnabled;

    /// <summary>
    /// The ONE CatalogType to BaseItemKind item-kind map (JF-954.8): the
    /// sync's wiring-table rows (Kind column and the generic rows' fetch
    /// kinds) and the snapshot export's fetch dispatch both read it, so the
    /// type-to-kind relation has one owner and a future synced type's kind
    /// lookup fails LOUDLY here (ArgumentOutOfRangeException at the first
    /// consumer) instead of silently disagreeing between the upload and the
    /// export. The types roster itself derives from CatalogSlotTypeNames
    /// (BuildCatalogSnapshot), which the JF-711 one-row contract keeps in
    /// lockstep with the sync's wiring-table rows; a new type is the one-row
    /// edit PLUS this map's arm, both in the same sitting.
    /// </summary>
    /// <param name="type">The catalog type.</param>
    /// <returns>The library item kind the sync fetches for it.</returns>
    internal static BaseItemKind CatalogTypeItemKind(CatalogType type) => type switch
    {
        CatalogType.Artist => BaseItemKind.MusicArtist,
        CatalogType.Album => BaseItemKind.MusicAlbum,
        CatalogType.Series => BaseItemKind.Series,
        CatalogType.Audiobook => BaseItemKind.AudioBook,
        CatalogType.Movie => BaseItemKind.Movie,
        CatalogType.Song => BaseItemKind.Audio,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no catalog item source for this type")
    };

    /// <summary>
    /// Resolve which locales to sync based on the config string.
    /// - "*": all active locales (from SMAPI manifest; the CONFIG default for
    ///   CatalogSyncLocales is "*", with the JF-543 ar-SA exclusion applied by
    ///   the caller).
    /// - Empty: it-IT only.
    /// - "de-DE,en-US,...": it-IT + the listed locales.
    /// JF-954.8: the branch shape and the list production live in the shared
    /// <see cref="ResolveLocaleList"/> parser (the snapshot export's
    /// <see cref="ResolveSnapshotLocales"/> passes its SMAPI-free embedded
    /// roster to the same parser); this method keeps only the SMAPI manifest
    /// read a "*" config needs.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ResolveSyncLocalesAsync(
        string syncLocalesConfig,
        string accessToken,
        string skillId,
        CancellationToken cancellationToken)
    {
        // The star test is null-safe so <see cref="ResolveLocaleList"/> stays
        // the ONE owner of the empty-config guard and the it-IT seed: a null
        // or empty config never matches "*", so no SMAPI call happens either
        // way and the parser returns it-IT alone.
        IEnumerable<string> starRoster = string.Equals(syncLocalesConfig?.Trim(), "*", StringComparison.Ordinal)
            ? await GetActiveLocalesAsync(accessToken, skillId, cancellationToken).ConfigureAwait(false)
            : Enumerable.Empty<string>();

        return ResolveLocaleList(syncLocalesConfig, starRoster);
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

/// <summary>
/// The shared final-payload build's result (JF-954.8): the payload plus the
/// drop record the sync's truncation warning reports. The library/static
/// composition split is the cut's own (TruncateTo returns it); this record
/// only carries what <see cref="CatalogPayload.TruncateTo"/> gave back, so
/// the warning's arithmetic reads exactly as it did before the extraction.
/// </summary>
/// <param name="Payload">The final catalog payload (library values, then the appended static tail, truncated to the cap).</param>
/// <param name="Dropped">The dropped entries in drop order; empty when the payload fit.</param>
/// <param name="DroppedStaticCount">How many of the dropped entries were static seed entries.</param>
internal sealed record FinalCatalogPayload(
    CatalogPayload Payload,
    IReadOnlyList<CatalogValue> Dropped,
    int DroppedStaticCount);
