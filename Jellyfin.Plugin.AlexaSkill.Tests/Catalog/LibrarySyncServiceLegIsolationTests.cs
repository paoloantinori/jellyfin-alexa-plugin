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
    private const string Base = "https://api.amazonalexa.com";
    private const string SimulatedDriftMessage = "simulated drifted enrichment path (JF-695 isolation pin)";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;
    private readonly List<(LogLevel Level, string Message)> _logCapture;

    public LibrarySyncServiceLegIsolationTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _smapiHandler = new FakeSmapiHandler();
        _logCapture = new List<(LogLevel, string)>();
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(_logCapture)));

        var catalogManager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(_smapiHandler)),
            _loggerFactory.CreateLogger<CatalogManager>());

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
        Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
    }

    public void Dispose()
    {
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
        Assert.Equal(new[] { CatalogType.Artist }, result.FrozenTypes);

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
        Assert.Equal(new[] { CatalogType.Artist }, first.FrozenTypes);
        Assert.Equal(new[] { CatalogType.Artist }, second.FrozenTypes);

        // The frozen type minted nothing across BOTH runs; the healthy types
        // re-minted per run (the payload-hash skip is per run, not persisted).
        Assert.Equal(0, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(AlbumCatalogId));
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(SeriesCatalogId));
        Assert.Null(user.ArtistCatalogId);
    }

    /// <summary>
    /// Extracts one slot-type node from a raw interaction-model JSON body.
    /// Clone() keeps the element valid after the owning document is disposed.
    /// </summary>
    private static JsonElement GetTypeNode(string modelJson, string typeName)
    {
        using var doc = JsonDocument.Parse(modelJson);
        return doc.RootElement
            .GetProperty("interactionModel")
            .GetProperty("languageModel")
            .GetProperty("types")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == typeName)
            .Clone();
    }

    /// <summary>
    /// Fake SMAPI backend serving all three catalog types: catalog creation
    /// (per-type id derived from the requested catalog name), version upload
    /// (202 + poll location), poll (SUCCEEDED v1), skill status, interaction
    /// model GET (static seeds for JellyfinArtist/AlbumName/SeriesName) and PUT.
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

        private static string CatalogIdForName(string body) =>
            body.Contains("Jellyfin Artists", StringComparison.Ordinal) ? ArtistCatalogId
            : body.Contains("Jellyfin Albums", StringComparison.Ordinal) ? AlbumCatalogId
            : body.Contains("Jellyfin Series", StringComparison.Ordinal) ? SeriesCatalogId
            : "amzn1.catalog.test.unknown";

        private static string ModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayArtistSongsIntent","slots":[{"name":"musician","type":"JELLYFIN_ARTIST"}]},{"name":"PlayAlbumIntent","slots":[{"name":"album","type":"AlbumName"}]},{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"JellyfinArtist","values":[{"name":{"value":"Mina"}}]},{"name":"AlbumName","values":[{"name":{"value":"Thriller"}}]},{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
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
                string catalogId = url.Split('/')[^3];
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/updateRequest/req-1") }
                };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/status", StringComparison.Ordinal))
            {
                return Json("""{"manifest":{"lastUpdateRequest":{"status":"SUCCEEDED"}},"interactionModel":{"it-IT":{"lastUpdateRequest":{"status":"SUCCEEDED"}}}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/", StringComparison.Ordinal))
            {
                return Json("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"1"}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                return Json(ModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                LastModelPutBody = body;
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
