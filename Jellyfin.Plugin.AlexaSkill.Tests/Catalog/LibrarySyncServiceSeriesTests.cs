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
/// Series catalog sync tests (JF-493). Unlike <see cref="LibrarySyncServiceTests"/>
/// (empty-item queries, no HTTP), these run the FULL sync against a fake SMAPI
/// HTTP handler so the catalog creation, version upload, ID persistence and
/// interaction-model injection of the series catalog are all exercised through
/// the real CatalogManager.
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceSeriesTests : PluginTestBase, IDisposable
{
    private const string SeriesCatalogId = "amzn1.catalog.test.series-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;

    public LibrarySyncServiceSeriesTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _smapiHandler = new FakeSmapiHandler(SeriesCatalogId);
        _loggerFactory = LoggerFactory.Create(b => { });

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
            "alexa-series-sync-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser()
    {
        return new Entities.User
        {
            Id = Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            SmapiDeviceToken = new DeviceToken("access-token", "refresh-token", "Bearer", 9999999999),
            UserSkill = new Entities.UserSkill { SkillId = "amzn1.ask.skill.test-id" },
            VendorId = "test-vendor-id",
            AllowedLibraryIds = null
        };
    }

    private void SetupLibraryWithSeries(params string[] seriesNames)
    {
        // Only the Series query returns items; artist/album queries return empty,
        // so all observed SMAPI traffic is series-catalog traffic.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.Series) == true
                ? seriesNames.Select(n => (BaseItem)new Series { Name = n, Id = Guid.NewGuid() }).ToList()
                : new List<BaseItem>());
    }

    /// <summary>
    /// A single series-only sync must create the series catalog ("Jellyfin Series"),
    /// persist its ID on the user, upload a catalog version, and inject the
    /// catalog-backed SeriesName type into the interaction model (replacing the
    /// static seed definition).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithSeries_CreatesSeriesCatalog_PersistsId_InjectsIntoModel()
    {
        // Arrange
        SetupLibraryWithSeries("Adolescence", "Breaking Bad");
        var user = CreateUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.SeriesCount);
        Assert.Equal(0, result.ArtistCount);
        Assert.Equal(0, result.AlbumCount);

        // Exactly one catalog creation, and it is the series catalog.
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        var createBody = _smapiHandler.CatalogCreationBodies.Single();
        Assert.Contains("Jellyfin Series", createBody, StringComparison.Ordinal);

        // The catalog ID is persisted on the user (XmlSerializer-safe string field).
        Assert.Equal(SeriesCatalogId, user.SeriesCatalogId);

        // The version upload targeted the created catalog.
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(SeriesCatalogId));

        // The interaction model PUT replaced the static SeriesName seed with the
        // catalog-backed type definition.
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        using var doc = JsonDocument.Parse(_smapiHandler.LastModelPutBody!);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        var seriesType = types.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "SeriesName");
        var catalog = seriesType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal(SeriesCatalogId, catalog.GetProperty("catalogId").GetString());
        Assert.False(seriesType.TryGetProperty("values", out _), "static seed values must be replaced, not kept alongside the valueSupplier");
    }

    /// <summary>
    /// A second sync must REUSE the persisted series catalog ID: no new catalog
    /// creation, and the version upload targets the same catalog.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SecondSync_ReusesPersistedSeriesCatalogId()
    {
        // Arrange
        SetupLibraryWithSeries("Adolescence");
        var user = CreateUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        string firstCatalogId = user.SeriesCatalogId!;
        Assert.Equal(SeriesCatalogId, firstCatalogId);

        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(SeriesCatalogId));
        Assert.Equal(firstCatalogId, user.SeriesCatalogId);
    }

    /// <summary>
    /// A user with NO series (and no artists/albums) must not create any catalog:
    /// the early-return guard covers the series count too.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithoutSeries_SkipsCatalogCreation()
    {
        // Arrange
        SetupLibraryWithSeries();
        var user = CreateUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, _smapiHandler.CatalogCreationCount);
        Assert.Null(user.SeriesCatalogId);
    }

    /// <summary>
    /// JF-495: the catalog sync's model PUT must land in the per-locale status
    /// ledger with the dedicated source label. The fake PUT carries no Location
    /// header, so this also exercises the skill-status build-tracking fallback.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_RecordsModelPutInLocaleLedger()
    {
        // Arrange
        SetupLibraryWithSeries("Adolescence");
        var user = CreateUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// JF-495: when the PUT's async build is tracked via its Location header and
    /// the post-deploy canary matches (the live model echoes the PUT body), the
    /// ledger records SUCCEEDED with no error.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_TrackedBuild_CanaryMatch_RecordsSucceeded()
    {
        // Arrange
        _smapiHandler.TrackModelBuild = true;
        SetupLibraryWithSeries("Adolescence");
        var user = CreateUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// JF-495 stale-pin guard: an entity type with ZERO items this run (series here)
    /// must NOT forward its stored catalog id with a null version; that made the
    /// injection pin the stale fallback version "1". The PUT must leave the static
    /// SeriesName seed untouched (no valueSupplier) while still injecting the types
    /// whose catalogs were refreshed (artist here).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_EmptyEntityType_DoesNotPinStaleCatalogVersion()
    {
        // Arrange: artists only; the user already carries a stored series catalog id.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true
                ? new List<BaseItem> { new MusicArtist { Name = "Mina", Id = Guid.NewGuid() } }
                : new List<BaseItem>());
        var user = CreateUser();
        user.SeriesCatalogId = "amzn1.catalog.test.stored-series";
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        using var doc = JsonDocument.Parse(_smapiHandler.LastModelPutBody!);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        var seriesType = types.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "SeriesName");
        Assert.False(seriesType.TryGetProperty("valueSupplier", out _),
            "an entity type with no fresh version must not have its stored catalog id pinned");

        var artistType = types.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "JellyfinArtist");
        Assert.Equal(user.ArtistCatalogId, artistType.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    /// <summary>
    /// Fake SMAPI backend. Serves the minimum surface LibrarySyncService touches:
    /// catalog creation, catalog version upload (202 + poll location), poll
    /// (SUCCEEDED), skill status, interaction model GET (static SeriesName seed;
    /// later GETs echo the last PUT body so the canary matches) and PUT
    /// (optionally 202 + poll location when <see cref="TrackModelBuild"/> is set).
    /// </summary>
    private sealed class FakeSmapiHandler : HttpMessageHandler
    {
        private readonly string _catalogId;
        private const string Base = "https://api.amazonalexa.com";
        private int _modelGetCount;

        public FakeSmapiHandler(string catalogId)
        {
            _catalogId = catalogId;
        }

        /// <summary>When true, model PUTs answer 202 + a pollable update-request location (JF-495).</summary>
        public bool TrackModelBuild { get; set; }

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public int CatalogCreationCount =>
            Requests.Count(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal));

        public List<string> CatalogCreationBodies =>
            Requests.Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal))
                .Select(r => r.Body ?? string.Empty)
                .ToList();

        public int VersionUploadsFor(string catalogId) =>
            Requests.Count(r => r.Method == HttpMethod.Post
                && r.Url == $"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");

        public string? LastModelPutBody { get; private set; }

        private static string ModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
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
                return Json($"{{\"catalogId\":\"{_catalogId}\"}}");
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/versions", StringComparison.Ordinal))
            {
                // 202 Accepted with a poll location, mirroring SMAPI's async build.
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{_catalogId}/updateRequest/req-1") }
                };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/stages/development/status", StringComparison.Ordinal))
            {
                // JF-495 build-settle wait: nothing in progress.
                return Json("""{"manifest":{"lastUpdateRequest":{"status":"SUCCEEDED"}},"interactionModel":{"it-IT":{"lastUpdateRequest":{"status":"SUCCEEDED"}}}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/", StringComparison.Ordinal))
            {
                return Json("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"1"}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                _modelGetCount++;
                if (TrackModelBuild && _modelGetCount > 1 && LastModelPutBody != null)
                {
                    // Post-deploy canary GET: echo what was PUT.
                    return Json(LastModelPutBody);
                }

                return Json(ModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                LastModelPutBody = body;
                if (TrackModelBuild)
                {
                    return new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Headers = { Location = new Uri($"{Base}/v1/skills/skill-1/stages/development/interactionModel/updateRequest/req-model-1") }
                    };
                }

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
