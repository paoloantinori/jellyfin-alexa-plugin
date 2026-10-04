#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
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
}
