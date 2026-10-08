#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-695 per-type sync-leg isolation pins. A deterministic payload-build
/// invariant violation (<see cref="CatalogPayloadInvariantException"/>, e.g. the
/// JF-689 enrichment guard drifting inside CatalogPayload.FromItems) must freeze
/// ONLY its own catalog type: no version minted, no catalog id forwarded to the
/// model injection (last-good stays pinned), while the sibling types sync and the
/// locale's model injection still runs. Non-invariant failures keep the
/// whole-leg handling (the 401 refresh-retry path is pinned by
/// LibrarySyncServiceSeriesTests.SyncUserLibraryAsync_MidSync401_NoRefreshAvailable_FailsLegAfterSingleAttempt).
/// The violations are simulated through the TypeLegEntryProbeForTest seam because
/// the real factory cannot produce them by construction (AppendTo and
/// AssertArtistEnrichment re-derive from the same PartialNameSynonyms.Generate).
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceLegIsolationTests : PluginTestBase, IDisposable
{
    private const string ArtistCatalogId = "amzn1.catalog.test.artist-1";
    private const string AlbumCatalogId = "amzn1.catalog.test.album-1";
    private const string SeriesCatalogId = "amzn1.catalog.test.series-1";
    private const string AudiobookCatalogId = "amzn1.catalog.test.audiobook-1";
    private const string Base = "https://api.amazonalexa.com";
    private const string SimulatedDriftMessage = "simulated drifted enrichment path (JF-695 isolation pin)";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;
    private readonly List<(LogLevel Level, string Message)> _logCapture;
    private readonly string _originalCatalogSyncLocales;

    public LibrarySyncServiceLegIsolationTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _smapiHandler = new FakeSmapiHandler();
        _logCapture = new List<(LogLevel, string)>();
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(_logCapture)));

        var catalogManager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(_smapiHandler)),
            _loggerFactory.CreateLogger<CatalogManager>())
        {
            // The fake backend answers instantly (JF-725 seam).
            PollDelayMsForTest = 0
        };

        _service = new LibrarySyncService(
            _libraryManagerMock.Object,
            catalogManager,
            _loggerFactory.CreateLogger<LibrarySyncService>());

        // SyncCatalogForLocaleAsync reads Plugin.Instance.Configuration.ServerAddress
        // when building the hosted catalog URL.
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-leg-isolation-test");

        // Hermeticity: the default CatalogSyncLocales ("*") would make every sync
        // call issue a real manifest GET to api.amazonalexa.com (with the fake
        // token, falling back to it-IT only via the generic catch). Pin the
        // locale set so the suite never leaves the fake (code-review F1).
        _originalCatalogSyncLocales = Plugin.Instance!.Configuration.CatalogSyncLocales;
        Plugin.Instance.Configuration.CatalogSyncLocales = string.Empty;
    }

    public void Dispose()
    {
        // Restore the shared plugin instance's locale override: xUnit runs
        // classes in arbitrary order, and a leaked string.Empty here would
        // silently pin every later sync test to it-IT-only.
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.CatalogSyncLocales = _originalCatalogSyncLocales;
        }

        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser() => TestHelpers.CreateSyncUser();

    /// <summary>
    /// Shared arrange for the freeze pins: the artist type's payload build
    /// deterministically fails its invariant at leg entry (the seam simulates
    /// the drifted enrichment path the real factory cannot produce).
    /// </summary>
    private void FreezeArtistTypeViaProbe() =>
        _service.TypeLegEntryProbeForTest = type =>
        {
            if (type == CatalogType.Artist)
            {
                throw new CatalogPayloadInvariantException(SimulatedDriftMessage);
            }
        };

    /// <summary>
    /// Shared arrange for the all-frozen pins (JF-709): every catalog type's
    /// payload build deterministically fails its invariant, so no version mints,
    /// the injection gate skips, and the leg completes with zero PUTs.
    /// </summary>
    private void FreezeAllTypesViaProbe() =>
        _service.TypeLegEntryProbeForTest = type =>
            throw new CatalogPayloadInvariantException(SimulatedDriftMessage);

    private void SetupLibraryWithAllTypes()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?[0] switch
            {
                BaseItemKind.MusicArtist => new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() } },
                BaseItemKind.MusicAlbum => new List<BaseItem> { new MusicAlbum { Name = "Dark Side of the Moon", Id = Guid.NewGuid() } },
                BaseItemKind.Series => new List<BaseItem> { new Series { Name = "Breaking Bad", Id = Guid.NewGuid() } },
                _ => new List<BaseItem>()
            });
    }

    /// <summary>
    /// The core isolation pin: an artist payload build that deterministically
    /// violates its construction invariant freezes ONLY the artist type. Album
    /// and series still mint versions, the model PUT still happens, and the PUT
    /// wires the album/series catalogs while the artist slot type keeps its
    /// pre-existing definition (its id was never forwarded, so its last-good
    /// catalog reference survives - the same JF-495 null-version rule the
    /// zero-items shape uses). The run reports the partial failure honestly:
    /// Success=false with Artist in FrozenTypes.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_ArtistInvariantViolation_FreezesOnlyArtistType()
    {
        // Arrange
        SetupLibraryWithAllTypes();
        FreezeArtistTypeViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: partial failure is honest.
        Assert.False(result.Success);
        // Order-independent: FrozenTypes is a set, its enumeration order is not a contract.
        Assert.Single(result.FrozenTypes);
        Assert.Contains(CatalogType.Artist, result.FrozenTypes);

        // The frozen type minted nothing: no catalog creation, no version upload,
        // no id persisted (the invariant fires inside the payload build, before
        // any SMAPI write for the type).
        Assert.Equal(0, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.DoesNotContain(_smapiHandler.CatalogCreationBodies, b => b.Contains("Jellyfin Artists", StringComparison.Ordinal));
        Assert.Null(user.ArtistCatalogId);

        // The sibling types synced normally: catalogs created, ids persisted,
        // versions minted (and pinned into the model below).
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(AlbumCatalogId));
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(SeriesCatalogId));
        Assert.Equal(AlbumCatalogId, user.AlbumCatalogId);
        Assert.Equal(SeriesCatalogId, user.SeriesCatalogId);

        // The model injection survived: the PUT happened, wires album+series,
        // and leaves the artist slot type untouched.
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        Assert.False(
            GetTypeNode(_smapiHandler.LastModelPutBody!, "JellyfinArtist").TryGetProperty("valueSupplier", out _),
            "the frozen artist type must not have its catalog id pinned by this run");
        Assert.Equal(
            AlbumCatalogId,
            GetTypeNode(_smapiHandler.LastModelPutBody!, "AlbumName").GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal(
            SeriesCatalogId,
            GetTypeNode(_smapiHandler.LastModelPutBody!, "SeriesName").GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());

        // The locale ledger still records the model PUT (it succeeded for the
        // types that were injected).
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);

        // The freeze is logged as an error naming the type.
        Assert.Contains(_logCapture, l => l.Level == LogLevel.Error
            && l.Message.Contains("violated a construction invariant", StringComparison.Ordinal)
            && l.Message.Contains("Artist", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-711 write-back pin: on a clean first sync, each catalog type's
    /// created id must land on its OWN user field through the per-type wiring
    /// table's threaded setter (the getter/setter pair that replaced the
    /// CatalogType-keyed if/else in SyncCatalogForLocaleAsync). Each row's
    /// setter must hit its own field: a swapped or dropped row setter
    /// misroutes an id, its getter then stays null, and every later run
    /// re-creates that catalog (SMAPI quota burn), the compile-silent
    /// missed-one shape this task closed at the table arity. The PUT body is
    /// asserted too, closing the two shapes the field asserts alone miss: a
    /// getter bound to a DIFFERENT field than its setter (fields pass, but
    /// the minted pair re-reads the wrong getter and forwards a null id), and
    /// a swapped row Type (fields pass, but the minted table is keyed by the
    /// wrong CatalogType). The negative direction (a type that never reaches
    /// creation stores nothing) is pinned above by the isolation test's
    /// user.ArtistCatalogId null assert.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesCreated_PersistsEachTypeCatalogId()
    {
        // Arrange: all three types have items, nothing is frozen, it-IT only.
        SetupLibraryWithAllTypes();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: every row's write-back landed on its own field (the fake
        // mints a distinct id per catalog name, so a cross-wired setter
        // cannot pass this).
        Assert.True(result.Success);
        Assert.Equal(ArtistCatalogId, user.ArtistCatalogId);
        Assert.Equal(AlbumCatalogId, user.AlbumCatalogId);
        Assert.Equal(SeriesCatalogId, user.SeriesCatalogId);

        // And each row's getter/setter pair and Type key agree: the model PUT
        // wires all three slot types with their own catalog ids (a misbound
        // getter or swapped Type forwards a null id, leaving that slot type's
        // static seed in place instead of a valueSupplier).
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        Assert.Equal(
            ArtistCatalogId,
            GetTypeNode(_smapiHandler.LastModelPutBody!, "JellyfinArtist").GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal(
            AlbumCatalogId,
            GetTypeNode(_smapiHandler.LastModelPutBody!, "AlbumName").GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal(
            SeriesCatalogId,
            GetTypeNode(_smapiHandler.LastModelPutBody!, "SeriesName").GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    /// <summary>
    /// Isolation must not swallow the leg-level machinery: a NON-invariant
    /// failure in one type (here an HttpRequestException from the album leg,
    /// simulating a network/SMAPI failure) still fails the WHOLE locale leg -
    /// no model PUT, no freeze recorded - exactly as before JF-695. The artist
    /// upload that completed before the failure is observable traffic, proving
    /// the types ran in order and nothing was isolated.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_NonInvariantTypeFailure_FailsWholeLeg()
    {
        // Arrange
        SetupLibraryWithAllTypes();
        _service.TypeLegEntryProbeForTest = type =>
        {
            if (type == CatalogType.Album)
            {
                throw new HttpRequestException("simulated non-invariant SMAPI failure (JF-695 pin)");
            }
        };
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: the whole leg failed (leg-level generic catch), no isolation.
        Assert.False(result.Success);
        Assert.Empty(result.FrozenTypes);
        Assert.Null(_smapiHandler.LastModelPutBody);
        Assert.Contains(_logCapture, l => l.Message.Contains("Catalog sync failed for locale", StringComparison.Ordinal));

        // Artist ran and uploaded before the album failure; it is traffic, not a
        // pinned version (the leg failed before the model injection).
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.Equal(0, _smapiHandler.VersionUploadsFor(AlbumCatalogId));
    }

    /// <summary>
    /// Freeze semantics for the failing type are per-RUN deterministic (the
    /// JF-689 contract, kept by JF-695): a second sync run freezes the artist
    /// type again while the healthy types re-sync normally. The frozen type
    /// never degrades to a partial upload.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_FrozenType_StaysFrozenOnSecondSync()
    {
        // Arrange
        SetupLibraryWithAllTypes();
        FreezeArtistTypeViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var first = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        var second = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Single(first.FrozenTypes);
        Assert.Contains(CatalogType.Artist, first.FrozenTypes);
        Assert.Single(second.FrozenTypes);
        Assert.Contains(CatalogType.Artist, second.FrozenTypes);

        // The frozen type minted nothing across BOTH runs; the healthy types
        // re-minted per run (the payload-hash skip is per run, not persisted).
        Assert.Equal(0, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(AlbumCatalogId));
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(SeriesCatalogId));
        Assert.Null(user.ArtistCatalogId);
    }

    /// <summary>
    /// JF-705 ledger observability pin: a partially frozen leg whose model PUT
    /// succeeded must not read as a clean locale in the per-locale status
    /// ledger. Status stays the PUT's own outcome (SUCCEEDED: the build DID
    /// succeed for the injected types, and the diagnostics panel's
    /// ModelsDeployed/failedModels status-string matches stay truthful), while
    /// the frozen types ride the entry's structured caveat (JF-721: the
    /// FrozenCatalogs bit plus the names payload), which config.html renders
    /// unconditionally next to the status icon. The clean re-run (probe
    /// cleared, same service and user) overwrites the entry with no caveat,
    /// pinning that the caveat is freeze-driven, not unconditional.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_PartialFreezeWithSuccessfulPut_LedgerSurfacesFrozenTypes()
    {
        // Arrange: artist freezes at leg entry, album/series mint, the model
        // PUT still runs and succeeds.
        SetupLibraryWithAllTypes();
        FreezeArtistTypeViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: the entry keeps the PUT's own outcome but names the frozen
        // type structurally (JF-721: fields, not a composed Error string).
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, ledger.Caveat);
        Assert.Equal("Artist", ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);

        // Clean leg: the fresh entry for the same locale must carry no frozen
        // caveat (the canary matches against the fake backend, so Error is null).
        _service.TypeLegEntryProbeForTest = null;
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal("SUCCEEDED", ledger!.Status);
        Assert.Equal(CatalogLedgerCaveats.None, ledger.Caveat);
        Assert.Null(ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// Covers the remaining two branches of the JF-705 caveat under one leg:
    /// TWO frozen types (the plural clause and the " + " join in the rendered
    /// caveat) plus a post-PUT canary mismatch (the free-text shape). The
    /// clause and the canary live in SEPARATE fields since JF-721, so the old
    /// clause-leads-within-80-chars constraint is structural (the caveat renders
    /// as its own span before the Error); this pin keeps the rendered caveat
    /// text honest instead. Status still records the PUT's own outcome: the
    /// build succeeded, only the verification mismatched.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_PartialFreezeWithCanaryError_LedgerCarriesCaveatBesideCanary()
    {
        // Arrange: artist AND album freeze at leg entry, series still mints, the
        // model PUT succeeds, and the canary reads back a model whose sample
        // count differs from the submission.
        SetupLibraryWithAllTypes();
        _service.TypeLegEntryProbeForTest = type =>
        {
            if (type == CatalogType.Artist || type == CatalogType.Album)
            {
                throw new CatalogPayloadInvariantException(SimulatedDriftMessage);
            }
        };
        _smapiHandler.ServeCanaryMismatchAfterPut = true;
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: the freeze still gates the run while the ledger entry keeps
        // the PUT's own outcome.
        Assert.False(result.Success);
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);

        // The structured caveat names both frozen types (payload CSV), the
        // free-text Error carries ONLY the canary, and the rendered caveat
        // (what config.html shows) is the plural clause with the " + " join.
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, ledger.Caveat);
        Assert.Equal("Artist,Album", ledger.FrozenCatalogTypes);
        Assert.NotNull(ledger.Error);
        Assert.Contains("canary mismatch", ledger.Error!, StringComparison.Ordinal);
        Assert.Equal("Artist + Album catalogs FROZEN (last-good pinned)", ledger.CaveatText);
    }

    /// <summary>
    /// JF-709 core pin: an ALL-FROZEN leg performs no model PUT, and without the
    /// leg-boundary write the locale would keep the PREVIOUS run's green
    /// SUCCEEDED ledger row while every catalog type is pinned last-good. The
    /// write must REPLACE that stale row and PRESERVE its Status (the live model
    /// on Amazon is unchanged by this run, so its
    /// recorded build status is still true, and the diagnostics panel's
    /// ModelsDeployed stays truthful for single-locale setups); the freeze rides
    /// the structured caveat (JF-721: FrozenCatalogs | NoCatalogPut plus the
    /// names payload) on a fresh LastUpdated, and the previous row's free-text
    /// diagnostic is carried VERBATIM in Error (no framing since JF-721), so a
    /// preserved non-SUCCEEDED status does not lose its failure reason
    /// (code-review F3). The clean re-run overwrites with no caveat, pinning
    /// that the caveat is freeze-driven (JF-705's #4 second half, extended to
    /// this path).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_PreExistingGreenEntry_PreservedWithFrozenClause()
    {
        // Arrange: a previous healthy run's green entry, then everything freezes.
        var seededTime = DateTime.UtcNow.AddHours(-1);
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = seededTime,
            Source = "Embedded",
            Error = "simulated prior diagnostic"
        });
        SetupLibraryWithAllTypes();
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: the run is honest (run-level), no PUT happened.
        Assert.False(result.Success);
        Assert.Equal(3, result.FrozenTypes.Count);
        Assert.Null(_smapiHandler.LastModelPutBody);

        // The stale green row was reached and replaced: Status preserved, the
        // freeze in the caveat bits with the names payload, the prior diagnostic
        // carried ATTRIBUTED in Error (gate-marker F3: a prior-run failure must
        // not read as the current run's), on a fresh timestamp. Source is always
        // this writer's own label (catalog sync authors the row, code-review F4).
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal("SUCCEEDED", ledger!.Status);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger.Source);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, ledger.Caveat);
        Assert.Equal("Artist,Album,Series", ledger.FrozenCatalogTypes);
        Assert.Equal($"{LibrarySyncService.CarriedDiagnosticLedgerPrefix}simulated prior diagnostic", ledger.Error);
        Assert.True(ledger.LastUpdated > seededTime, "the replaced entry must carry a fresh timestamp");

        // Clean re-run: the entry carries no caveat (the freeze is the trigger).
        _service.TypeLegEntryProbeForTest = null;
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal("SUCCEEDED", ledger!.Status);
        Assert.Equal(CatalogLedgerCaveats.None, ledger.Caveat);
        Assert.Null(ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// JF-709: the all-frozen shape with NO previous entry (fresh install,
    /// everything frozen on the first run) must still write an entry - silence
    /// here is the same observability hole. With nothing truthful to preserve,
    /// the entry reads Status "Skipped" (the documented no-PUT meaning, extended
    /// by JF-709) with the freeze caveat bits and no free-text Error (nothing
    /// to carry) and the catalog-sync source.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_NoPriorEntry_LedgerRecordsSkipped()
    {
        SetupLibraryWithAllTypes();
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, result.FrozenTypes.Count);
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal("Skipped", ledger!.Status);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger.Source);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, ledger.Caveat);
        Assert.Equal("Artist,Album,Series", ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// JF-703 addendum pin (fix folded into JF-709): the payload hash must be
    /// recorded only AFTER a successful upload. Sequence: attempt 1's artist
    /// version upload gets a 401, the leg-level refresh-retry runs attempt 2;
    /// with the old record-before-upload order attempt 2 hash-skipped the never-
    /// minted artist version, the PUT omitted the artist catalog, and the leg
    /// read as a clean completion. The pin discriminates: the artist version
    /// upload runs TWICE (401 + real), the PUT carries the artist catalog id,
    /// and the locale reports SUCCEEDED with all three catalogs wired.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_ArtistUpload401_RetriedAttempt_ReuploadsUnmintedVersion()
    {
        SetupLibraryWithAllTypes();
        _smapiHandler.FailVersionUpload401OnceForCatalogId = ArtistCatalogId;
        var user = CreateUser();
        user.SmapiRefreshToken = "refresh-token";
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // The 401 retry refreshes via LWA; serve it locally (the SmapiTokenRefresherTests
        // pattern). Credentials and the override are restored so the shared plugin
        // instance never leaks them.
        Plugin.Instance!.Configuration.LwaClientId = "test-client-id";
        Plugin.Instance!.Configuration.LwaClientSecret = "test-client-secret";
        LwaClient.HttpClientOverrideForTests = () => new HttpClient(new Lwa.SmapiTokenRefresherTests.HappyHandler());
        try
        {
            var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

            // Attempt 2 RE-uploaded the artist version (401 POST + real POST),
            // instead of hash-skipping the version attempt 1 never minted.
            Assert.Equal(2, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
            Assert.Equal(1, _smapiHandler.VersionUpload401sServed);

            // The PUT happened and wires ALL THREE catalogs, artist included.
            Assert.NotNull(_smapiHandler.LastModelPutBody);
            Assert.Equal(
                ArtistCatalogId,
                GetTypeNode(_smapiHandler.LastModelPutBody!, "JellyfinArtist")
                    .GetProperty("valueSupplier").GetProperty("valueCatalog")
                    .GetProperty("catalogId").GetString());

            Assert.True(result.Success);
            var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
            Assert.NotNull(ledger);
            Assert.Equal("SUCCEEDED", ledger!.Status);
            Assert.Null(ledger.Error);
        }
        finally
        {
            LwaClient.HttpClientOverrideForTests = null;
            Plugin.Instance!.Configuration.LwaClientId = string.Empty;
            Plugin.Instance!.Configuration.LwaClientSecret = string.Empty;
        }
    }

    /// <summary>
    /// Rework pins F5 + F3 for the no-PUT writer: (a) two CONSECUTIVE all-frozen
    /// runs must not nest this writer's own message; since JF-721 the no-nesting
    /// rule is field-keyed (the first run's NoCatalogPut bit makes the second
    /// run replace wholesale, so the carried foreign Error drops to null: no
    /// chain, no growth, structurally), which matters because the JF-695 cadence
    /// re-runs the full sync on EVERY restart while the drift persists; (b) a
    /// TRANSIENT previous status (IN_PROGRESS, the startup capture's poll
    /// world) clamps to "Skipped" instead of being preserved forever, while a
    /// foreign previous Error is carried once on the first run (verbatim,
    /// unframed) and dropped on the second.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_ConsecutiveRuns_ErrorStaysSingleDepth()
    {
        SetupLibraryWithAllTypes();
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "IN_PROGRESS",
            LastUpdated = DateTime.UtcNow.AddHours(-1),
            Source = "Embedded",
            Error = "simulated in-flight build"
        });
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var first = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        var entryAfterFirst = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");

        // F3: the transient status clamped to the healthy neutral, and the
        // foreign diagnostic carried exactly once, attributed (gate-marker
        // rework F3: the prefix marks it a prior-run diagnostic).
        Assert.False(first.Success);
        Assert.NotNull(entryAfterFirst);
        Assert.Equal("Skipped", entryAfterFirst!.Status);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, entryAfterFirst.Caveat);
        Assert.Equal($"{LibrarySyncService.CarriedDiagnosticLedgerPrefix}simulated in-flight build", entryAfterFirst.Error);

        // Second all-frozen run with the probe still in place (the every-restart
        // cadence): the entry must NOT nest or grow.
        var second = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        var entryAfterSecond = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");

        Assert.False(second.Success);
        Assert.NotNull(entryAfterSecond);
        Assert.Equal("Skipped", entryAfterSecond!.Status);

        // Single depth, no growth: the same caveat bits and payload, and the
        // once-carried foreign diagnostic is DROPPED (own-shape replace keyed on
        // the first run's NoCatalogPut bit) rather than re-carried.
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, entryAfterSecond.Caveat);
        Assert.Equal(entryAfterFirst.FrozenCatalogTypes, entryAfterSecond.FrozenCatalogTypes);
        Assert.Null(entryAfterSecond.Error);
    }

    /// <summary>
    /// JF-710 coordination-note hazard pin (field form since JF-721): the
    /// startup capture preserves the FrozenCatalogs bit WITHOUT the
    /// run-scoped NoCatalogPut bit (a new skill version was pushed, so the
    /// no-PUT shape drops). A caveat-blind own-shape check would classify that
    /// preserved row as foreign and carry its diagnostic forever. The predicate
    /// must recognize the frozen-caveat shape, so the next all-frozen run
    /// REPLACES the row with exactly its own fields.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_CapturePreservedClauseRow_ReplacedWithoutSelfReferentialTrail()
    {
        // Arrange: the row exactly as the JF-710 capture preserve leaves it
        // (frozen caveat, no no-PUT bit, catalog-sync source).
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-1),
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Caveat = CatalogLedgerCaveats.FrozenCatalogs,
            FrozenCatalogTypes = "Artist"
        });
        SetupLibraryWithAllTypes();
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert: exactly this writer's own row (its caveat bits and payload,
        // nothing carried), no self-referential trail.
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, ledger.Caveat);
        Assert.Equal("Artist,Album,Series", ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// Accepted consequence of the frozen-caveat own-shape arm (the JF-709
    /// review rejected the strip-at-marker alternative): a foreign diagnostic
    /// riding a frozen row (the JF-705 PUT writer's frozen + canary shape,
    /// which the capture preserve also mints) is REPLACED by the next
    /// all-frozen run, not carried. Its durable home is the capture preserve;
    /// this pin keeps the replace explicit rather than accidental.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_ClauseLedRowWithCanary_ReplacedEntirely()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-1),
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Caveat = CatalogLedgerCaveats.FrozenCatalogs,
            FrozenCatalogTypes = "Artist",
            Error = "canary mismatch: submitted 145/900 but live reports 144/899"
        });
        SetupLibraryWithAllTypes();
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, ledger.Caveat);
        Assert.Equal("Artist,Album,Series", ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// Gate-marker rework F1, writer half: a pre-JF-721 persisted sync row
    /// carries its composed Error TEXT (clause + no-PUT tail baked in) with NO
    /// caveat bits, so the fields read it as a foreign diagnostic and this
    /// writer would carry the text VERBATIM onto a caveat-carrying row,
    /// displaying "no PUT this run" beside this run's own fresh clause (the
    /// duplication the coordinator's finding names). The carry migrates the
    /// legacy text once: the stale clause/tail drop, the fresh caveat carries
    /// the freeze, and only a real foreign diagnostic (none here) would ride
    /// attributed.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AllTypesFrozen_LegacyComposedRow_CarriesMigratedTextOnly()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-1),
            Source = LibrarySyncService.CatalogSyncLedgerSource,
            Error = "Artist + Album + Series catalogs FROZEN (last-good pinned); no PUT this run"
        });
        SetupLibraryWithAllTypes();
        FreezeAllTypesViaProbe();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.NoCatalogPut, ledger.Caveat);
        Assert.Equal("Artist,Album,Series", ledger.FrozenCatalogTypes);
        Assert.Null(ledger.Error);

        // The rendered tail is THIS run's own truthful wording (the leg really
        // performed no PUT); what must never survive is the legacy text in the
        // Error, asserted above as null.
        Assert.Equal(
            "Artist + Album + Series catalogs FROZEN (last-good pinned); no PUT this run",
            ledger.CaveatText);
    }

    /// <summary>
    /// Extracts one slot-type node from a raw interaction-model JSON body.
    /// Delegates to the hoisted TestHelpers.GetModelTypeNode (JF-717).
    /// </summary>
    private static JsonElement GetTypeNode(string modelJson, string typeName)
        => TestHelpers.GetModelTypeNode(modelJson, typeName);

    /// <summary>
    /// Fake SMAPI backend serving all three catalog types: catalog creation
    /// (per-type id derived from the requested catalog name), version upload
    /// (202 + poll location), poll (SUCCEEDED v1), skill status, interaction
    /// model GET (static seeds for JellyfinArtist/AlbumName/SeriesName) and PUT.
    /// Per-file by policy (JF-725): the family's full-sync fakes diverge in MODE
    /// knobs, not values (catalog-id derivation, version numbering, 401-injection
    /// target/cardinality, PUT response shape, model-GET-after-PUT semantics
    /// (static seeds + the canary-mismatch variant here vs echo-last-PUT in
    /// Series vs static-seed in EquivalenceClass), PUT capture (last body vs
    /// per-locale dictionary), status-map composition), so the
    /// hoist-on-third convention fires only on identical leaf constructions (the
    /// status map delegates to TestHelpers.SmapiSkillStatusJson), never on the
    /// routing surface.
    /// </summary>
    private sealed class FakeSmapiHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public List<string> CatalogCreationBodies =>
            Requests.Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal))
                .Select(r => r.Body ?? string.Empty)
                .ToList();

        public int VersionUploadsFor(string catalogId) =>
            Requests.Count(r => r.Method == HttpMethod.Post
                && r.Url == $"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");

        public string? LastModelPutBody { get; private set; }

        /// <summary>
        /// When true, GETs of the interaction-model URL AFTER the first PUT
        /// serve a variant whose sample counts differ from the submission, so
        /// the post-PUT canary reports a mismatch (JF-705 pin for the combined
        /// Error message). The pre-PUT GET still serves the base model.
        /// </summary>
        public bool ServeCanaryMismatchAfterPut { get; set; }

        /// <summary>
        /// JF-709/JF-703 addendum pin: when set to a catalog id, the FIRST version
        /// upload POST for that catalog returns 401 (subsequent ones succeed), so
        /// the leg's 401 refresh-retry re-runs the type leg.
        /// </summary>
        public string? FailVersionUpload401OnceForCatalogId { get; set; }

        public int VersionUpload401sServed { get; private set; }

        private bool _modelPutSeen;

        private static string CatalogIdForName(string body) =>
            body.Contains("Jellyfin Artists", StringComparison.Ordinal) ? ArtistCatalogId
            : body.Contains("Jellyfin Albums", StringComparison.Ordinal) ? AlbumCatalogId
            : body.Contains("Jellyfin Series", StringComparison.Ordinal) ? SeriesCatalogId
            : body.Contains("Jellyfin Audiobooks", StringComparison.Ordinal) ? AudiobookCatalogId
            : "amzn1.catalog.test.unknown";

        private static string ModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayArtistSongsIntent","slots":[{"name":"musician","type":"JELLYFIN_ARTIST"}]},{"name":"PlayAlbumIntent","slots":[{"name":"album","type":"AlbumName"}]},{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"JellyfinArtist","values":[{"name":{"value":"Mina"}}]},{"name":"AlbumName","values":[{"name":{"value":"Thriller"}}]},{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
            """;

        /// <summary>Same model with two samples on the first intent: the canary's
        /// sample count (2) differs from the submitted one (0).</summary>
        private static string CanaryMismatchModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayArtistSongsIntent","samples":["suona mina","metti mina"],"slots":[{"name":"musician","type":"JELLYFIN_ARTIST"}]},{"name":"PlayAlbumIntent","slots":[{"name":"album","type":"AlbumName"}]},{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"JellyfinArtist","values":[{"name":{"value":"Mina"}}]},{"name":"AlbumName","values":[{"name":{"value":"Thriller"}}]},{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url, body));

            if (request.Method == HttpMethod.Post && url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal))
            {
                return Json($"{{\"catalogId\":\"{CatalogIdForName(body ?? string.Empty)}\"}}");
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/versions", StringComparison.Ordinal))
            {
                string catalogId = url.Split('/')[^2];

                // JF-709/JF-703 addendum pin: the FIRST version upload for this
                // catalog 401s (the leg-level refresh-retry then re-runs it).
                if (catalogId == FailVersionUpload401OnceForCatalogId && VersionUpload401sServed == 0)
                {
                    VersionUpload401sServed++;
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"message\":\"Token is invalid/expired.\"}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/updateRequest/req-1") }
                };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/status", StringComparison.Ordinal))
            {
                return Json(TestHelpers.SmapiSkillStatusJson("it-IT"));
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/", StringComparison.Ordinal))
            {
                return Json("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"1"}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                return Json(ServeCanaryMismatchAfterPut && _modelPutSeen ? CanaryMismatchModelJson : ModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                LastModelPutBody = body;
                _modelPutSeen = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"error\":\"unexpected {request.Method} {url}\"}}", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
