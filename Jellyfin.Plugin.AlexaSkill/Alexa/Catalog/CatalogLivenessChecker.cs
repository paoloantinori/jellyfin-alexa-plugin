using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Microsoft.Extensions.Logging;
using Refit;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The plugin-owned catalog liveness check (JF-966). GETs each stored SMAPI
/// catalog id via the ManagementApi's ReferenceCatalogManagement typed client
/// (the SDK's call on the SAME route the catalogs were created on,
/// /v1/skills/api/custom/interactionModel/catalogs/{catalogId}) and turns a
/// clean 404 into a LOUD degradation signal: an error log naming the dead
/// catalog(s), a durable CatalogIdsDead caveat on every locale-ledger row, and
/// (behind the default-off CatalogAutoHealDeadIds flag) the documented recovery
/// (null the dead ids, clear LastCatalogSync so the next scheduled sync
/// recreates them). Run from CatalogSyncTask's skip path: the &lt;12h gate skip
/// costs zero SMAPI calls today, so this piggyback adds at most six reads per
/// skipped user, only for users WITH credentials, and zero for a user with no
/// stored ids.
/// <para>
/// TRIAGE RULE (why this check exists; the 2026-11-25 deletion incident's
/// first hour): a whole slot-type family dying cross-locale is an
/// INFRASTRUCTURE signature (catalog ids 404ing on Amazon), NOT an NLU
/// hypothesis. Check catalog liveness BEFORE graph/lab-battery/conjunction
/// theories: that hour went to JF-964 conjunction probing while every catalog
/// was already 404 and LocaleModelStatuses still read SUCCEEDED.
/// </para>
/// <para>
/// Verdict discrimination is deliberate: ONLY a clean 404 marks dead. A network
/// error, a 401 (expired token), a 5xx, anything else is UNKNOWN and writes
/// NOTHING to the ledger: an unknown must never be able to wipe ids (the
/// auto-heal null is data loss on a false positive) nor fake a caveat. Dead is
/// a fact; unknown is silence.
/// </para>
/// <para>
/// Ledger scope note: the locale ledger is plugin-global and locale-keyed, not
/// user-keyed (the observation family handles multi-user via ObservedSkillId,
/// but sync-authored rows carry null). With more than one linked user the
/// caveat describes the most recent liveness outcome; the single-household
/// reality this plugin ships for makes the distinction moot, and loud-on-every
/// surface beats silent-healthy (the incident's failure mode).
/// </para>
/// </summary>
public class CatalogLivenessChecker
{
    /// <summary>Ledger Source label for rows the liveness check authored, the
    /// same convention as LibrarySyncService.CatalogSyncLedgerSource: the row
    /// names its writer family. The capture's own-content recognition accepts
    /// the row through its caveat-bits arm (the bit is non-None), so the
    /// durable caveat survives clean startup captures; a cleared (bit-less)
    /// liveness row is deliberately NOT preserved by that arm, because a
    /// healthy check has nothing durable to say.</summary>
    internal const string CatalogLivenessLedgerSource = "CatalogLivenessCheck";

    private readonly ILogger<CatalogLivenessChecker> _logger;

    public CatalogLivenessChecker(ILogger<CatalogLivenessChecker> logger)
    {
        _logger = logger;
    }

    /// <summary>A stored catalog id and the user record field it came from.</summary>
    /// <param name="Type">The catalog type; rendered via ToString() into logs
    /// and the ledger Error, so the display vocabulary stays the enum's.</param>
    /// <param name="CatalogId">The stored SMAPI catalog id; null is filtered
    /// at construction (a catalog that was never created is not dead).</param>
    /// <param name="Clear">Clears the user field (the auto-heal action).</param>
    internal readonly record struct StoredCatalog(CatalogType Type, string? CatalogId, Action Clear);

    /// <summary>
    /// The six stored catalog ids of one user, in the enum's canonical order.
    /// Null fields are skipped: a catalog that was never created is not dead,
    /// so a user with no stored ids at all costs ZERO SMAPI calls (and no
    /// Clear closure is allocated for an unset field). Completeness is pinned
    /// by StoredCatalogs_CoversEveryStoredCatalogIdProperty (tests): a new
    /// User *CatalogId property without a row here fails that test instead of
    /// compiling silent (the JF-711/JF-727 one-row-edit doctrine's lighter
    /// half; the field map itself stays the sync table's).
    /// </summary>
    private static List<StoredCatalog> StoredCatalogs(Entities.User user)
    {
        List<StoredCatalog> stored = new(6);
        AddIfSet(stored, CatalogType.Artist, user.ArtistCatalogId, () => user.ArtistCatalogId = null);
        AddIfSet(stored, CatalogType.Album, user.AlbumCatalogId, () => user.AlbumCatalogId = null);
        AddIfSet(stored, CatalogType.Series, user.SeriesCatalogId, () => user.SeriesCatalogId = null);
        AddIfSet(stored, CatalogType.Audiobook, user.AudiobookCatalogId, () => user.AudiobookCatalogId = null);
        AddIfSet(stored, CatalogType.Movie, user.MovieCatalogId, () => user.MovieCatalogId = null);
        AddIfSet(stored, CatalogType.Song, user.SongCatalogId, () => user.SongCatalogId = null);
        return stored;
    }

    /// <summary>Adds the row when the id is set. Takes the ALREADY-READ value
    /// (the accessor pin allows the checker at most one GETTER call per
    /// property, so the guard and the stored value share one read).</summary>
    private static void AddIfSet(List<StoredCatalog> stored, CatalogType type, string? catalogId, Action clear)
    {
        if (!string.IsNullOrWhiteSpace(catalogId))
        {
            stored.Add(new(type, catalogId, clear));
        }
    }

    /// <summary>One probe's verdict. Dead requires a clean 404; everything else
    /// that fails is Unknown (never treated as dead).</summary>
    internal enum ProbeVerdict
    {
        /// <summary>The catalog answered.</summary>
        Alive,

        /// <summary>The catalog 404'd: gone on Amazon's side.</summary>
        Dead,

        /// <summary>The probe failed some other way (network, auth, 5xx, timeout):
        /// silence, not death.</summary>
        Unknown
    }

    /// <summary>
    /// Checks one user's stored catalog ids. Called from CatalogSyncTask's
    /// &lt;12h skip path (the only regularly-executing surface a stopped sync
    /// still walks), so dead catalogs surface between syncs instead of waiting
    /// out the 12h gate. Never throws for a probe failure (unknowns log and
    /// move on); ledger writes are non-fatal per the shared writer convention.
    /// </summary>
    /// <param name="user">The plugin user whose stored ids to probe. Its
    /// <see cref="Entities.User.SmapiManagement"/> carries the test seam.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CheckUserAsync(Entities.User user, CancellationToken cancellationToken)
    {
        List<StoredCatalog> stored = StoredCatalogs(user);
        if (stored.Count == 0)
        {
            return;
        }

        SmapiManagement? smapi = user.SmapiManagement;
        if (smapi == null)
        {
            return;
        }

        var dead = new List<StoredCatalog>();
        var unknown = new List<string>();
        bool healed = false;
        foreach (StoredCatalog catalog in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeVerdict verdict = await ProbeAsync(user, catalog.CatalogId!, cancellationToken).ConfigureAwait(false);
            switch (verdict)
            {
                case ProbeVerdict.Dead:
                    dead.Add(catalog);
                    break;
                case ProbeVerdict.Unknown:
                    unknown.Add(catalog.Type.ToString());
                    break;
            }
        }

        if (dead.Count > 0)
        {
            string deadList = string.Join(", ", dead.Select(c => $"{c.Type}={c.CatalogId}"));
            _logger.LogError(
                "SMAPI catalog liveness FAILED for user {UserId}: {DeadCount} stored catalog id(s) returned 404: {DeadIds} (JF-966). "
                + "Voice selection for these slot types is dead on Amazon's side while the plugin's own surfaces report healthy. "
                + "A whole slot-type family dying cross-locale is an INFRASTRUCTURE signature (dead catalogs), NOT an NLU regression: "
                + "check catalog liveness BEFORE NLU/graph/conjunction theories.",
                user.Id,
                dead.Count,
                deadList);

            // The flag read guards like the ledger writer below (a null plugin
            // instance degrades to report-only instead of throwing exactly on
            // the dead finding).
            var config = Plugin.Instance?.Configuration;
            if (config != null && config.CatalogAutoHealDeadIds)
            {
                HealDeadIds(user, dead);
                healed = true;
            }

            // One save per check (hasUnsavedHealState): the heal mutations and
            // the ledger rows land in the same in-memory Configuration, so a
            // single locked save carries both instead of two full serializations.
            WriteLedgerCaveat(deadList, clear: false, hasUnsavedHealState: healed);
        }
        else if (unknown.Count > 0)
        {
            // Unknown is NOT dead (the class doc's discrimination rule): log so
            // the failure is visible, touch nothing.
            _logger.LogWarning(
                "SMAPI catalog liveness UNKNOWN for user {UserId}: probe(s) failed with a non-404 error for {UnknownTypes} (JF-966); "
                + "the ledger stays untouched (only a clean 404 marks a catalog dead)",
                user.Id,
                string.Join(", ", unknown));
        }
        else
        {
            WriteLedgerCaveat(deadList: null, clear: true);
        }
    }

    /// <summary>
    /// One get-catalog probe. The call rides the shared JF-545 auth-recovery
    /// wrapper (<see cref="AlexaUtil.CallAsync"/>: one refresh-and-retry on a
    /// 401, the func re-resolving the user's SmapiManagement so the retry uses
    /// the refreshed token), the same choke point every other SMAPI family
    /// goes through; the typed <c>ReferenceCatalogManagement.Get</c> is the
    /// SDK's call on the same route the catalogs were created on. A non-success
    /// status surfaces as <see cref="ApiException"/>: a clean 404 is caught
    /// with StatusCode NotFound (dead), and any other failure
    /// (HttpRequestException, a 401 that survived the refresh, a 5xx, a
    /// timeout) is Unknown. The response body is unused: completion without
    /// exception IS the liveness signal.
    /// </summary>
    internal async Task<ProbeVerdict> ProbeAsync(Entities.User user, string catalogId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await AlexaUtil.CallAsync(
                user,
                () => user.SmapiManagement!.ReferenceCatalogManagement.Get(catalogId)).ConfigureAwait(false);
            return ProbeVerdict.Alive;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown raced the probe; propagate (the caller's skip path
            // handles cancellation the same way the sync path does).
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation(
                "SMAPI catalog {CatalogId} returned 404 (JF-966): the stored id names a catalog that no longer exists on Amazon",
                catalogId);
            return ProbeVerdict.Dead;
        }
        catch (Exception ex)
        {
            // The Unknown arm's whole point: a failed probe must never read as
            // dead, so every non-404 failure lands here and stays silent on
            // the ledger.
            _logger.LogDebug(
                ex,
                "SMAPI catalog liveness probe for {CatalogId} failed with a non-404 error (JF-966); verdict UNKNOWN",
                catalogId);
            return ProbeVerdict.Unknown;
        }
    }

    /// <summary>
    /// The auto-heal arm (CatalogAutoHealDeadIds, DEFAULT OFF): nulls exactly
    /// the dead ids and clears LastCatalogSync, the documented force-next-sync
    /// action (the same null the ConfigurationController's JF-946 reset
    /// writes), so the next scheduled pass recreates the catalogs and PUTs the
    /// models. NO sync is triggered inline: the state stays broken until that
    /// pass runs, which is why the loud error and the ledger caveat still
    /// fire. Mutations only; the caller owns the one persist and reports the
    /// unsaved-heal state itself (the arm always mutates when it runs).
    /// </summary>
    private void HealDeadIds(Entities.User user, List<StoredCatalog> dead)
    {
        foreach (StoredCatalog catalog in dead)
        {
            catalog.Clear();
        }

        user.LastCatalogSync = null;

        _logger.LogWarning(
            "Catalog auto-heal (CatalogAutoHealDeadIds): nulled {DeadCount} dead catalog id(s) for user {UserId} and cleared LastCatalogSync "
            + "so the next scheduled catalog sync recreates them (JF-966). No sync was started inline.",
            dead.Count,
            user.Id);
    }

    /// <summary>
    /// The ledger writer: on a dead finding, stamps the CatalogIdsDead caveat
    /// onto EVERY existing locale row (the JF-951 no-PUT writer's wholesale
    /// replace: Status keeps its settled value via
    /// <see cref="LibrarySyncService.PreservedOrSkippedStatus"/>, Error carries
    /// the dead ids, Source names this check). On an all-alive pass, clears the
    /// bit from rows that carry it (and only those rows: the compose declines
    /// everything else, so a healthy ledger's clear pass costs one snapshot
    /// read and zero row writes). Unknown findings never reach this method.
    /// Saves ONCE when anything changed (rows written, or unsaved auto-heal
    /// state rides along), through the ONE locked save path (JF-724), the same
    /// convention as the other ledger writers; the guard/catch mirror
    /// LibrarySyncService.WriteLedgerEntry's shell.
    /// Accepted residual: the locale LIST is swept from one pre-loop snapshot,
    /// so a row ADDED mid-sweep by a concurrent writer misses this pass's
    /// stamp until the next pass re-derives it (multi-user same-pass timing;
    /// the compose itself always sees the current row).
    /// </summary>
    private void WriteLedgerCaveat(string? deadList, bool clear, bool hasUnsavedHealState = false)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            IReadOnlyList<LocaleModelStatusEntry> snapshot = config.GetLocaleModelStatusSnapshot();

            // The healthy steady state (every restart for every user whose
            // catalogs are fine): the snapshot already knows no row carries
            // the bit, so skip the per-row locked compose pass entirely.
            if (clear && snapshot.All(e => !e.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead)))
            {
                return;
            }

            // Loop-invariant compose choice, hoisted out of the row sweep.
            Func<LocaleModelStatus?, LocaleModelStatus?> compose = clear
                ? ClearCompose
                : previous => DeadCompose(previous, deadList!);

            bool wrote = false;
            foreach (LocaleModelStatusEntry entry in snapshot)
            {
                wrote |= config.UpdateLocaleModelStatus(entry.Locale, compose) != null;
            }

            if (!wrote && !hasUnsavedHealState)
            {
                return;
            }

            config.PersistUnderLedgerLock();

            if (clear)
            {
                _logger.LogInformation(
                    "SMAPI catalog liveness recovered: every stored catalog id answered; the CatalogIdsDead caveat cleared from the locale ledger (JF-966)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record the catalog liveness outcome (dead-catalog auto-heal and/or ledger caveat; non-fatal, the next pass re-derives both) (JF-966)");
        }
    }

    /// <summary>The dead-write row: wholesale replace per the JF-951 no-PUT
    /// writer's precedent, WITH that writer's carry doctrine (JF-709 rework
    /// F1): a previous row that is NOT one of the subsystem's own shapes
    /// (<see cref="LibrarySyncService.IsOwnShapeLedgerCaveat"/>) carries its
    /// diagnostic ATTRIBUTED beside the dead-ids text, migrated once if it is
    /// still a pre-JF-721 composed text, so a foreign diagnostic (a canary
    /// mismatch, SMAPI build errors) never silently vanishes under the
    /// rewrite. Own-shape rows replace wholesale (the fresh dead text
    /// refreshes the row). The previous FrozenCatalogs bit and names payload
    /// are NOT carried either way: the check cannot reconstruct which types
    /// froze, and bit-iff-payload is the writers' factory rule; the next real
    /// sync PUT rewrites the row wholesale regardless.</summary>
    private static LocaleModelStatus DeadCompose(LocaleModelStatus? previous, string deadList)
    {
        string deadText = $"catalog liveness: 404 on stored catalog id(s): {deadList}; "
            + "slot-type selection for these catalogs is dead until the catalogs re-sync (JF-966)";

        string? carried = null;
        if (previous?.Error is { Length: > 0 } foreignError
            && !LibrarySyncService.IsOwnShapeLedgerCaveat(previous.Caveat))
        {
            carried = LibrarySyncService.MigrateLegacyLedgerError(foreignError).Error is { Length: > 0 } migrated
                ? LibrarySyncService.CarriedDiagnosticLedgerPrefix + migrated
                : null;
        }

        return new LocaleModelStatus
        {
            Status = LibrarySyncService.PreservedOrSkippedStatus(previous?.Status),
            LastUpdated = DateTime.UtcNow,
            Error = carried == null ? deadText : deadText + "; " + carried,
            Caveat = CatalogLedgerCaveats.CatalogIdsDead,
            FrozenCatalogTypes = null,
            Source = CatalogLivenessLedgerSource,
            ObservedSkillId = null,
        };
    }

    /// <summary>The clear-write row: declines (null) every row that does not
    /// carry the bit; strips it where it does, and clears the check's own
    /// Error only when nothing durable remains (the row was wholly ours; a
    /// hand-authored residual caveat keeps its text).</summary>
    private static LocaleModelStatus? ClearCompose(LocaleModelStatus? previous)
    {
        if (previous == null || !previous.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead))
        {
            return null;
        }

        var caveat = previous.Caveat & ~CatalogLedgerCaveats.CatalogIdsDead;
        bool ownShape = caveat == CatalogLedgerCaveats.None;
        return new LocaleModelStatus
        {
            Status = previous.Status,
            LastUpdated = DateTime.UtcNow,
            Error = ownShape ? null : previous.Error,
            Caveat = caveat,
            FrozenCatalogTypes = ownShape ? null : previous.FrozenCatalogTypes,
            Source = ownShape ? CatalogLivenessLedgerSource : previous.Source,
            ObservedSkillId = previous.ObservedSkillId,
        };
    }
}
