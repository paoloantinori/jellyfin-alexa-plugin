#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-721 field-design contracts, pinned at the DTO and endpoint level (the
/// ledger test families pin the writers end to end; these pin the PERSISTED
/// shape itself): the caveat bits and the frozen-types payload survive the
/// XmlSerializer round trip (including a combined flags value), a pre-JF-721
/// persisted row (no Caveat / FrozenCatalogTypes elements in the XML)
/// deserializes caveat-less with its legacy Error text intact (the
/// LastPlayedLaunchRoute additive compat pattern), the admin-panel renderer
/// composes the three display-only phrases correctly, the capture preserve's
/// field-copy rules hold in isolation, and the status endpoint actually ships
/// the rendered caveat config.html reads.
/// </summary>
[Collection("Plugin")]
public class LocaleModelStatusCaveatTests : PluginTestBase, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigurationController _controller;

    public LocaleModelStatusCaveatTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-ledger-caveat-test");

        _controller = new ConfigurationController(
            Mock.Of<IUserManager>(),
            Mock.Of<ISessionManager>(),
            Mock.Of<ILibraryManager>(),
            _loggerFactory,
            new ModelDeploymentManager(
                Mock.Of<IHttpClientFactory>(),
                _loggerFactory.CreateLogger<ModelDeploymentManager>()),
            Mock.Of<IInteractionModelRedeployer>());
    }

    public void Dispose()
    {
        Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();
        _loggerFactory.Dispose();
    }

    private static LocaleModelStatus RoundTrip(LocaleModelStatus status)
    {
        var entry = new LocaleModelStatusEntry("it-IT", status);
        using var writer = new StringWriter();
        new XmlSerializer(typeof(LocaleModelStatusEntry)).Serialize(writer, entry);
        var deserialized = (LocaleModelStatusEntry)new XmlSerializer(typeof(LocaleModelStatusEntry))
            .Deserialize(new StringReader(writer.ToString()))!;
        return deserialized.ToStatus();
    }

    /// <summary>
    /// The whole point of the flags enum: combined caveat bits (the no-PUT
    /// writer's row) must survive the XML round trip bit-for-bit, with the
    /// names payload and the free-text Error alongside.
    /// </summary>
    [Fact]
    public void CaveatBits_AndPayload_RoundTripThroughXml()
    {
        var status = new LocaleModelStatus
        {
            Status = "Skipped",
            LastUpdated = DateTime.UtcNow,
            Error = "simulated prior diagnostic",
            Caveat = CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut,
            FrozenCatalogTypes = "Artist,Album,Series",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
        };

        var back = RoundTrip(status);

        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, back.Caveat);
        Assert.Equal("Artist,Album,Series", back.FrozenCatalogTypes);
        Assert.Equal("simulated prior diagnostic", back.Error);
        Assert.Equal(status.Status, back.Status);
        Assert.Equal(status.Source, back.Source);
        Assert.Equal(CatalogLedgerCaveats.ObservedBuildErrors, RoundTrip(new LocaleModelStatus
        {
            Status = "FAILED",
            Caveat = CatalogLedgerCaveats.ObservedBuildErrors,
        }).Caveat);
    }

    /// <summary>
    /// The additive compat contract: a pre-JF-721 persisted row's XML carries
    /// NO Caveat / FrozenCatalogTypes elements; it must deserialize caveat-less
    /// (None) with the legacy composed Error text intact, because the admin UI
    /// renders that text verbatim (its clause is baked into it). The serialized
    /// XML here is hand-written to the pre-JF-721 element set so a future
    /// rename of the new elements cannot silently repoint this pin at the
    /// current writer output.
    /// </summary>
    [Fact]
    public void LegacyRowXml_WithoutCaveatElements_ReadsCaveatLessWithErrorIntact()
    {
        const string legacyXml = """
            <LocaleModelStatusEntry xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Locale>it-IT</Locale>
              <Status>SUCCEEDED</Status>
              <LastUpdated>2026-10-01T12:00:00Z</LastUpdated>
              <Error>Artist catalog FROZEN (last-good pinned); no PUT this run</Error>
              <Source>CatalogSyncGetModifyPut</Source>
            </LocaleModelStatusEntry>
            """;

        var entry = (LocaleModelStatusEntry)new XmlSerializer(typeof(LocaleModelStatusEntry))
            .Deserialize(new StringReader(legacyXml))!;
        var status = entry.ToStatus();

        Assert.Equal(CatalogLedgerCaveats.None, status.Caveat);
        Assert.Null(status.FrozenCatalogTypes);
        Assert.Equal("Artist catalog FROZEN (last-good pinned); no PUT this run", status.Error);
    }

    /// <summary>
    /// The display-only renderer: the admin panel's caveat text beside the
    /// free-text Error. JF-705's wording kept (singular/plural by type count,
    /// the " + " join), JF-709's tail, JF-722's arm label; null when no caveat
    /// applies. These strings are DISPLAY-ONLY since JF-721 (nothing parses
    /// them); this pin exists so the wording cannot drift unnoticed, not as a
    /// protocol contract.
    /// </summary>
    [Fact]
    public void CaveatText_RendersTheThreePhrases()
    {
        Assert.Null(new LocaleModelStatus { Status = "SUCCEEDED" }.CaveatText);

        Assert.Equal(
            "Artist catalog FROZEN (last-good pinned)",
            new LocaleModelStatus { Caveat = CatalogLedgerCaveats.FrozenCatalogs, FrozenCatalogTypes = "Artist" }.CaveatText);

        Assert.Equal(
            "Artist + Album + Series catalogs FROZEN (last-good pinned); no PUT this run",
            new LocaleModelStatus
            {
                Caveat = CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut,
                FrozenCatalogTypes = "Artist,Album,Series",
            }.CaveatText);

        Assert.Equal(
            "build errors",
            new LocaleModelStatus { Caveat = CatalogLedgerCaveats.ObservedBuildErrors, Error = "INVALID_SKILL_PACKAGE: boom" }.CaveatText);

        // The legacy-migrated bit without recoverable names renders nameless
        // (gate-marker rework F1): the freeze stays visible even when the
        // migration could not parse a names head.
        Assert.Equal(
            "catalogs FROZEN (last-good pinned)",
            new LocaleModelStatus { Caveat = CatalogLedgerCaveats.FrozenCatalogs }.CaveatText);
    }

    /// <summary>
    /// The one-shot legacy migration over the FULL pre-JF-721 composition
    /// grammar (gate-marker rework F1): every composed shape decomposes into
    /// the bare foreign diagnostic plus the names the clause carried (or null
    /// when it carried none), the run-scoped tail and framing drop, the
    /// own-errors shape clears, and field-era free text passes through
    /// untouched (the steady state once every persisted row is field-era).
    /// </summary>
    [Fact]
    public void MigrateLegacyLedgerError_DecomposesTheLegacyGrammar()
    {
        // PUT-writer shapes.
        Assert.Equal(
            (null, "Artist"),
            LibrarySyncService.MigrateLegacyLedgerError("Artist catalog FROZEN (last-good pinned)"));
        Assert.Equal(
            ("canary mismatch: submitted 145/900 but live reports 144/899", "Artist,Album"),
            LibrarySyncService.MigrateLegacyLedgerError(
                "Artist + Album catalogs FROZEN (last-good pinned); canary mismatch: submitted 145/900 but live reports 144/899"));

        // No-PUT writer shapes: the run-scoped tail and the framing drop.
        Assert.Equal(
            (null, "Artist"),
            LibrarySyncService.MigrateLegacyLedgerError("Artist catalog FROZEN (last-good pinned); no PUT this run"));
        Assert.Equal(
            ("simulated prior diagnostic", "Artist"),
            LibrarySyncService.MigrateLegacyLedgerError(
                "Artist catalog FROZEN (last-good pinned); no PUT this run; previous: simulated prior diagnostic"));

        // Own-errors shape: superseded observation-era text, clears.
        Assert.Equal(
            (null, null),
            LibrarySyncService.MigrateLegacyLedgerError("build errors: INVALID_SKILL_PACKAGE: boom"));

        // Field-era free text: untouched; nothing to migrate.
        Assert.Equal(
            ("canary mismatch: submitted 145/900 but live reports 144/899", null),
            LibrarySyncService.MigrateLegacyLedgerError("canary mismatch: submitted 145/900 but live reports 144/899"));
        Assert.Equal((null, null), LibrarySyncService.MigrateLegacyLedgerError(null));

        // Code-review refresh R1 hardening: a foreign diagnostic QUOTING the
        // clause marker mid-sentence (its head carries ':' or ';' and does not
        // end in the catalog noun) is not clause-led, so it passes through
        // untouched: no bogus FrozenCatalogs mint, no chopped text.
        const string quotedMarker =
            "upstream note: another vendor's catalogs FROZEN (last-good pinned) by their sync";
        Assert.Equal(
            (quotedMarker, null),
            LibrarySyncService.MigrateLegacyLedgerError(quotedMarker));

        // The remaining accepted window, pinned as documented behavior: a
        // foreign text carrying the bare tail literal alone is misdated and
        // stripped (the canary format and SMAPI messages never produce it;
        // heals at the next sync).
        Assert.Equal(
            ("canary mismatch: submitted 145/900 but live reports 144/899", null),
            LibrarySyncService.MigrateLegacyLedgerError(
                "canary mismatch: submitted 145/900 but live reports 144/899; no PUT this run"));
    }

    /// <summary>
    /// The capture preserve in isolation (the end-to-end families pin it
    /// through the capture): a pure FIELD COPY keyed on the Source label or
    /// any caveat bit, masking off the run-scoped NoCatalogPut bit, and
    /// dropping an ObservedBuildErrors-tagged Error with its bit (that Error
    /// describes a build the fresh capture supersedes; the drop rule's one
    /// owner, DropObservedBuildErrors, pinned at the pair level here).
    /// </summary>
    [Fact]
    public void PreserveLedgerCaveatAcrossCapture_CopiesFields_MasksRunScopedBit_NeverPreservesArmRows()
    {
        // Nothing to preserve: no row, or a row with neither key.
        Assert.Null(LibrarySyncService.PreserveLedgerCaveatAcrossCapture(null));
        Assert.Null(LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Source = "Embedded",
            Error = "stale prior build failure",
        }));

        // Catalog-authored row: the caveat and Error copy, NoCatalogPut drops.
        var preserved = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Error = "canary mismatch: submitted 145/900 but live reports 144/899",
            Caveat = CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut,
            FrozenCatalogTypes = "Artist",
        });
        Assert.NotNull(preserved);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, preserved!.Value.Caveat);
        Assert.Equal("Artist", preserved.Value.FrozenCatalogTypes);
        Assert.Equal("canary mismatch: submitted 145/900 but live reports 144/899", preserved.Value.Error);

        // Content-keyed (a previous capture's row carries no Source label, but
        // its copied bits are the key): idempotent across captures.
        var again = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "IN_PROGRESS",
            Source = "Embedded",
            Caveat = CatalogLedgerCaveats.FrozenCatalogs,
            FrozenCatalogTypes = "Artist",
        });
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, again!.Value.Caveat);
        Assert.Null(again.Value.Error);

        // The arm-tagged Error is superseded by the fresh build (the shared
        // DropObservedBuildErrors rule): the Error drops with its bit, even
        // under the catalog-sync Source (unreachable today, defensive shape).
        var armDropped = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "IN_PROGRESS",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Caveat = CatalogLedgerCaveats.ObservedBuildErrors,
            Error = "INVALID_SKILL_PACKAGE: previous build error",
        });
        Assert.NotNull(armDropped);
        Assert.Equal(CatalogLedgerCaveats.None, armDropped!.Value.Caveat);
        Assert.Null(armDropped.Value.Error);

        // The drop rule's ONE owner, pinned at the pair level (the refresh's
        // clean-settle arm routes through the same helper).
        var (droppedCaveat, droppedError) = LibrarySyncService.DropObservedBuildErrors(
            CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.ObservedBuildErrors,
            "INVALID_SKILL_PACKAGE: stale");
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, droppedCaveat);
        Assert.Null(droppedError);
        var (keptCaveat, keptError) = LibrarySyncService.DropObservedBuildErrors(
            CatalogLedgerCaveats.FrozenCatalogs,
            "canary mismatch: submitted 145/900 but live reports 144/899");
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, keptCaveat);
        Assert.Equal("canary mismatch: submitted 145/900 but live reports 144/899", keptError);

        // Bit-iff-payload on the COPY path too (code-review F2): a stray
        // payload whose FrozenCatalogs bit does not survive (here: a
        // NoCatalogPut-only row, never minted today) must not ride the
        // survivor; the factory's pair rule holds on preserves as well.
        var payloadDropped = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "Skipped",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Caveat = CatalogLedgerCaveats.NoCatalogPut,
            FrozenCatalogTypes = "Artist",
        });
        Assert.NotNull(payloadDropped);
        Assert.Equal(CatalogLedgerCaveats.None, payloadDropped!.Value.Caveat);
        Assert.Null(payloadDropped.Value.FrozenCatalogTypes);

        // Gate-marker rework F1: a pre-JF-721 persisted SYNC row (composed
        // text, no bits) migrates through the preserve: the clause mints the
        // frozen caveat with the names parsed from the text, the tail and
        // framing drop, and the bare foreign diagnostic survives. The
        // skip-gated capture chain can never resurrect the stale wording.
        var legacyPreserved = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Error = "Artist + Album catalogs FROZEN (last-good pinned); no PUT this run; previous: canary mismatch: submitted 145/900 but live reports 144/899",
        });
        Assert.NotNull(legacyPreserved);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, legacyPreserved!.Value.Caveat);
        Assert.Equal("Artist,Album", legacyPreserved.Value.FrozenCatalogTypes);
        Assert.Equal("canary mismatch: submitted 145/900 but live reports 144/899", legacyPreserved.Value.Error);
    }

    /// <summary>
    /// The config.html contract (JF-721): the admin-panel status endpoint ships
    /// the rendered caveat beside the free-text error, and a caveat-less row
    /// ships a null caveat so the UI renders the legacy Error alone. Driven
    /// through the REAL controller action so a dropped projection field cannot
    /// ship with only the C# writers green.
    /// </summary>
    [Fact]
    public void GetCustomModelStatus_ShipsRenderedCaveatBesideError()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "Skipped",
            Caveat = CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut,
            FrozenCatalogTypes = "Artist",
            Source = LibrarySyncService.CatalogSyncLedgerSource,
        });
        Plugin.Instance!.Configuration.SetLocaleModelStatus("en-US", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Source = "Embedded",
        });

        var result = _controller.GetCustomModelStatus();
        var json = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json.Value));
        var locales = doc.RootElement.GetProperty("localeModelStatuses");

        var it = locales.GetProperty("it-IT");
        Assert.Equal(
            "Artist catalog FROZEN (last-good pinned); no PUT this run",
            it.GetProperty("caveat").GetString());

        var en = locales.GetProperty("en-US");
        Assert.True(
            en.TryGetProperty("caveat", out var none) && none.ValueKind == JsonValueKind.Null,
            "a caveat-less row (incl. every pre-JF-721 persisted row) must ship a null caveat, not a missing field");
    }

    // ------------------------------------------------------------------
    // JF-724: the ledger accessors (lock, snapshot, atomic update) and the
    // family-membership defaults.
    // ------------------------------------------------------------------

    /// <summary>
    /// JF-724 item 1's mechanism: the snapshot is a STABLE COPY. A writer
    /// mutating the ledger (Add on a new locale, indexer-set on an existing
    /// one: both bump the live collection's version) after the snapshot was
    /// taken must not change what the snapshot says, because the admin
    /// endpoints derive every answer from the snapshot.
    /// </summary>
    [Fact]
    public void Snapshot_IsAStableCopy_WhileTheLiveLedgerMutates()
    {
        var config = Plugin.Instance!.Configuration;
        config.SetLocaleModelStatus("it-IT", new LocaleModelStatus { Status = "SUCCEEDED", Source = "Embedded" });

        var snapshot = config.GetLocaleModelStatusSnapshot();
        config.SetLocaleModelStatus("it-IT", new LocaleModelStatus { Status = "FAILED", Source = "Embedded" });
        config.SetLocaleModelStatus("en-US", new LocaleModelStatus { Status = "SUCCEEDED", Source = "Embedded" });

        Assert.Single(snapshot);
        Assert.Equal("SUCCEEDED", snapshot[0].Status);
        Assert.Equal("SUCCEEDED", snapshot[0].ToStatus().Status);
    }

    /// <summary>
    /// JF-724 item 3's contract: UpdateLocaleModelStatus hands the compose the
    /// CURRENT row and stores its product; a compose that returns null
    /// DECLINES and leaves the row untouched (the refresh's not-my-family
    /// shape). Together with the one-lock acquisition this is the atomic
    /// read-modify-write the capture preserve, the refresh settle, and the
    /// no-PUT carry now route through.
    /// </summary>
    [Fact]
    public void UpdateLocaleModelStatus_ComposesFromCurrentRow_AndDeclineLeavesItUntouched()
    {
        var config = Plugin.Instance!.Configuration;
        var seeded = DateTime.UtcNow.AddHours(-3);
        config.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "IN_PROGRESS",
            LastUpdated = seeded,
            Source = "Embedded",
        });

        LocaleModelStatus? seen = null;
        var written = config.UpdateLocaleModelStatus("it-IT", existing =>
        {
            seen = existing;
            return existing! with { Status = "SUCCEEDED" };
        });

        Assert.NotNull(seen!);
        Assert.Equal("IN_PROGRESS", seen!.Status);
        Assert.NotNull(written);
        Assert.Equal("SUCCEEDED", written!.Status);
        Assert.Equal("SUCCEEDED", config.GetLocaleModelStatus("it-IT")!.Status);

        var declined = config.UpdateLocaleModelStatus("it-IT", _ => null);
        Assert.Null(declined);
        var row = config.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);

        // A declined update on a locale with NO row must not mint one.
        Assert.Null(config.UpdateLocaleModelStatus("de-DE", _ => null));
        Assert.Null(config.GetLocaleModelStatus("de-DE"));
    }

    /// <summary>
    /// JF-724 item 4: the record's Source default is EMPTY, so a writer that
    /// composes a row without choosing a source lands OUTSIDE the capture
    /// family (the old "Embedded" default drafted omitting writers into the
    /// refresh's rewrite set, the JF-722 rework F5 gap); the ENTRY's default
    /// stays "Embedded" because it is the XML-compat default for pre-Source
    /// persisted rows. The asymmetry is deliberate and load-bearing.
    /// </summary>
    [Fact]
    public void SourceDefaults_RecordOmissionStaysOutsideTheCaptureFamily_EntryStaysEmbedded()
    {
        var omission = new LocaleModelStatus { Status = "IN_PROGRESS" };
        Assert.Equal(string.Empty, omission.Source);
        Assert.NotEqual(global::Jellyfin.Plugin.AlexaSkill.EntryPoints.SkillStartup.CaptureLedgerSource, omission.Source);

        Assert.Equal("Embedded", new LocaleModelStatusEntry().Source);
    }

    /// <summary>
    /// JF-724 items 2/5: the observation family's skill-attribution field is
    /// XML-additive. A stamped row round-trips; a pre-JF-724 persisted row (no
    /// element in the XML) reads null, which the refresh's family predicate
    /// treats as eligible for any refresh (legacy rows keep today's
    /// behavior); an old DLL ignores the element (rollback-safe).
    /// </summary>
    [Fact]
    public void ObservedSkillId_RoundTripsThroughXml_LegacyRowsReadNull()
    {
        var back = RoundTrip(new LocaleModelStatus
        {
            Status = "IN_PROGRESS",
            Source = "Embedded",
            ObservedSkillId = "amzn1.ask.skill.abc",
        });
        Assert.Equal("amzn1.ask.skill.abc", back.ObservedSkillId);

        const string preJf724Xml = """
            <LocaleModelStatusEntry xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Locale>it-IT</Locale>
              <Status>IN_PROGRESS</Status>
              <LastUpdated>2026-10-01T12:00:00Z</LastUpdated>
              <Source>Embedded</Source>
            </LocaleModelStatusEntry>
            """;
        var legacy = (LocaleModelStatusEntry)new XmlSerializer(typeof(LocaleModelStatusEntry))
            .Deserialize(new StringReader(preJf724Xml))!;
        Assert.Null(legacy.ObservedSkillId);
        Assert.Null(legacy.ToStatus().ObservedSkillId);
    }

    /// <summary>
    /// JF-724 item 1's behavior: the two admin surfaces that read the ledger
    /// (the setup panel and the custom-model status endpoint) must survive
    /// concurrent ledger writers. Pre-fix, both enumerated the LIVE
    /// collection, and a writer's Add/indexer-set bumping the version
    /// mid-enumeration threw InvalidOperationException and 500ed the surface;
    /// with the locked snapshot they can never observe a mutation. The stress
    /// keeps the readers polling for the WHOLE writer phase (bounded writer
    /// budgets, no reader loop of its own) over a ledger wide enough that the
    /// pre-fix enumeration windows were microseconds each, so a regression
    /// re-enumerating the live collection fails this reliably on a
    /// multi-threaded runner (verified red pre-fix on this suite's hardware).
    /// </summary>
    [Fact]
    public async Task AdminLedgerSurfaces_SurviveConcurrentLedgerWriters()
    {
        var config = Plugin.Instance!.Configuration;
        config.LocaleModelStatuses.Clear();
        var diagnostics = new DiagnosticsController(
            new RequestCounters(),
            new JellyfinConnectivityChecker(_loggerFactory.CreateLogger<JellyfinConnectivityChecker>()));

        const int locales = 150;
        const int writerTasks = 4;
        const int writesPerTask = 5_000;
        for (int l = 0; l < locales; l++)
        {
            config.SetLocaleModelStatus(
                $"stress-{l}",
                new LocaleModelStatus { Status = "SUCCEEDED", LastUpdated = DateTime.UtcNow, Source = "Embedded" });
        }

        var writersDone = new TaskCompletionSource();
        // ToList MATERIALIZES the sequences (Task.Run fires on enumeration):
        // without it both await points would start their tasks one at a time,
        // the readers after the writers finished, and the stress would pass
        // vacuously (the red-check caught exactly that).
        var writers = Enumerable.Range(0, writerTasks).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < writesPerTask; i++)
            {
                string locale = $"stress-{(w + i) % locales}";
                config.SetLocaleModelStatus(
                    locale,
                    new LocaleModelStatus { Status = i % 2 == 0 ? "SUCCEEDED" : "IN_PROGRESS", LastUpdated = DateTime.UtcNow, Source = "Embedded" });
            }
        })).ToList();
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!writersDone.Task.IsCompleted)
            {
                // The ORACLE is threefold: the call must not throw (an
                // enumeration fault surfaces at Task.WhenAll below), must be a
                // JsonResult, and must carry a payload (a regression that
                // swallowed the throw into a null/empty result cannot pass).
                var panel = Assert.IsType<JsonResult>(await diagnostics.GetPanel());
                Assert.NotNull(panel.Value);
                var status = Assert.IsType<JsonResult>(_controller.GetCustomModelStatus());
                Assert.NotNull(status.Value);
                await Task.Delay(1); // keep the spin from starving the writers
            }
        })).ToList();

        await Task.WhenAll(writers); // throws the first writer failure
        writersDone.SetResult();
        await Task.WhenAll(readers); // throws the first reader failure (the pre-fix InvalidOperationException)

        Assert.Equal(locales, config.GetLocaleModelStatusSnapshot().Count);
    }
}
