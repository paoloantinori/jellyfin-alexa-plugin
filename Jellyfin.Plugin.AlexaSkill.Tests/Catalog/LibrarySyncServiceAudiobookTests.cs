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
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// Audiobook catalog sync tests (JF-823), the <see cref="LibrarySyncServiceSeriesTests"/>
/// pattern: the FULL sync runs against a fake SMAPI HTTP handler so the catalog
/// creation, version upload, ID persistence and interaction-model injection of
/// the audiobook catalog are all exercised through the real CatalogManager.
/// The seed-survival pin reads the payload SMAPI would have fetched (the
/// CatalogController cache the sync stored the upload into) and proves the
/// it-IT static seed rides the upload alongside the library books: catalog
/// wiring is replace-in-place, so without the JF-823 seed arm the 22-value
/// it-IT AudiobookTitle block would vanish from the live vocabulary at first
/// sync (the JF-684 selection-gating fallback).
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceAudiobookTests : PluginTestBase, IDisposable
{
    private const string AudiobookCatalogId = "amzn1.catalog.test.audiobook-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _logCapture;

    public LibrarySyncServiceAudiobookTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _smapiHandler = new FakeSmapiHandler(AudiobookCatalogId);
        _logCapture = new List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
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
            _loggerFactory.CreateLogger<LibrarySyncService>())
        {
            // The fake backend needs no rate limiting (JF-717 seam).
            InterLocaleDelayMsForTest = 0
        };

        // SyncCatalogForLocaleAsync reads Plugin.Instance.Configuration.ServerAddress
        // when building the hosted catalog URL.
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-audiobook-sync-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser() => TestHelpers.CreateSyncUser();

    private void SetupLibraryWithAudiobooks(params string[] bookNames)
    {
        // Only the AudioBook query returns items; the other three types return
        // empty, so all observed SMAPI traffic is audiobook-catalog traffic.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.AudioBook) == true
                ? bookNames.Select(n => (BaseItem)new AudioBook { Name = n, Id = Guid.NewGuid() }).ToList()
                : new List<BaseItem>());
    }

    /// <summary>
    /// A single audiobook-only sync must create the audiobook catalog
    /// ("Jellyfin Audiobooks"), persist its ID on the user (JF-823's
    /// AudiobookCatalogId), upload a catalog version, set the count, and
    /// inject the catalog-backed AudiobookTitle type into the interaction
    /// model (replacing the static seed definition in place, no re-typing).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithAudiobooks_CreatesCatalog_PersistsId_InjectsIntoModel()
    {
        // Arrange
        SetupLibraryWithAudiobooks("Il Nome della Rosa", "Sapiens");
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.AudiobookCount);
        Assert.Equal(0, result.ArtistCount);
        Assert.Equal(0, result.AlbumCount);
        Assert.Equal(0, result.SeriesCount);

        // Exactly one catalog creation, and it is the audiobook catalog.
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        var createBody = _smapiHandler.CatalogCreationBodies.Single();
        Assert.Contains("Jellyfin Audiobooks", createBody, StringComparison.Ordinal);

        // The catalog ID is persisted on the user (XmlSerializer-safe string field).
        Assert.Equal(AudiobookCatalogId, user.AudiobookCatalogId);

        // The version upload targeted the created catalog.
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(AudiobookCatalogId));

        // The interaction model PUT replaced the static AudiobookTitle seed with
        // the catalog-backed type definition (the SeriesName replace-in-place
        // shape: the slot keeps its type, so no re-typing arm fired).
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        var audiobookType = TestHelpers.GetModelTypeNode(_smapiHandler.LastModelPutBody!, "AudiobookTitle");
        var catalog = audiobookType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal(AudiobookCatalogId, catalog.GetProperty("catalogId").GetString());
        Assert.False(audiobookType.TryGetProperty("values", out _), "static seed values must be replaced, not kept alongside the valueSupplier");
    }

    /// <summary>
    /// THE SEED-SURVIVAL PIN (JF-823): the payload the sync stores for SMAPI to
    /// fetch must carry the it-IT static seed titles the user's library does
    /// NOT hold, so the replace-in-place wiring (InjectCatalogReferences) that
    /// swaps the saved model's static type block for the catalog supplier
    /// cannot strip the seed from the live vocabulary at first sync. Library
    /// entries win on collision: a seed the library also holds appears once,
    /// under the library id.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_AudiobookPayload_CarriesTheItItSeed_BeyondTheLibrary()
    {
        // Arrange: only "Sapiens" is a seed the library holds; the rest are out
        // of library and out of seed.
        SetupLibraryWithAudiobooks("Sapiens", "A Library-Only Book");
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        string payload = ReadStoredPayloadContaining("A Library-Only Book");

        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        var byName = values.EnumerateArray()
            .GroupBy(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToDictionary(g => g.Key!, g => g.Single(), StringComparer.Ordinal);

        // The library book carries the jellyfin_audiobook_ id prefix (the
        // FormatId of the Audiobook type) - the payload builder consumed the
        // AudioBook items.
        Assert.Contains(values.EnumerateArray(), v =>
            v.GetProperty("name").GetProperty("value").GetString() == "A Library-Only Book"
            && v.GetProperty("id").GetString()!.StartsWith("jellyfin_audiobook_", StringComparison.Ordinal));

        // A seed title the library does NOT hold survived into the upload.
        Assert.Contains("Il Piccolo Principe", byName.Keys, StringComparer.Ordinal);
        Assert.Contains("Thinking Fast and Slow", byName.Keys, StringComparer.Ordinal);

        // The library-held seed is NOT duplicated: one entry, under the
        // library's real id (library wins on collision).
        var sapiens = values.EnumerateArray()
            .Single(v => v.GetProperty("name").GetProperty("value").GetString() == "Sapiens");
        Assert.StartsWith("jellyfin_audiobook_", sapiens.GetProperty("id").GetString(), StringComparison.Ordinal);

        // Every seed value the enrichment appends carries the deterministic
        // seed guid id shape, and the payload is exactly library + seeds with
        // the library-held seed suppressed (the exact 22-value set is pinned
        // in CatalogSeedEnrichmentTests).
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        Assert.Equal(1 + seedCount, values.GetArrayLength());
    }

    /// <summary>
    /// A second sync must REUSE the persisted audiobook catalog ID: no new
    /// catalog creation, and the version upload targets the same catalog.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SecondSync_ReusesPersistedAudiobookCatalogId()
    {
        // Arrange
        SetupLibraryWithAudiobooks("Sapiens");
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        string firstCatalogId = user.AudiobookCatalogId!;
        Assert.Equal(AudiobookCatalogId, firstCatalogId);

        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(AudiobookCatalogId));
        Assert.Equal(firstCatalogId, user.AudiobookCatalogId);
    }

    /// <summary>
    /// A user with NO audiobooks (and no artists/albums/series) must not create
    /// any catalog: the emptiness pre-check covers the fourth leg too.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithoutAudiobooks_SkipsCatalogCreation()
    {
        // Arrange
        SetupLibraryWithAudiobooks();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Act
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, _smapiHandler.CatalogCreationCount);
        Assert.Null(user.AudiobookCatalogId);
    }

    /// <summary>
    /// JF-543, the audiobook inheritance: the locale gate is model-wide
    /// (LibrarySyncService filters the locale before any per-type work), so the
    /// new wiring cannot reach ar-SA either. An ar-SA-only config runs the
    /// it-IT base leg only and no ar-SA traffic leaves the plugin.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_ExcludesCatalogWiringUnsupportedLocale_FromAllTraffic()
    {
        Plugin.Instance!.Configuration.CatalogSyncLocales = "ar-SA";
        try
        {
            SetupLibraryWithAudiobooks("Sapiens");
            var user = CreateUser();
            var jellyfinUser = TestHelpers.CreateJellyfinUser();

            var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

            Assert.True(result.Success);
            Assert.DoesNotContain(
                _smapiHandler.Requests,
                r => r.Url.Contains("/locales/ar-SA", StringComparison.Ordinal));
            Assert.Contains(
                _smapiHandler.Requests,
                r => r.Method == HttpMethod.Put && r.Url.EndsWith("/locales/it-IT", StringComparison.Ordinal));
        }
        finally
        {
            Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
        }
    }

    /// <summary>
    /// Reads the catalog payload the sync stored for SMAPI to fetch (the
    /// CatalogController cache), selecting the entry that contains the given
    /// marker. Reflection over the private static cache: the payload is what
    /// SMAPI would pull, so asserting on it is the closest thing to observing
    /// the actual upload without a server.
    /// </summary>
    private static string ReadStoredPayloadContaining(string marker)
    {
        var controllerType = typeof(CatalogController);
        var cacheField = controllerType.GetField("CatalogCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(cacheField != null, "CatalogController.CatalogCache must still exist (this pin's lookup target)");
        var cache = cacheField!.GetValue(null)!;

        var tryGetValue = cache.GetType().GetMethod("TryGetValue")!;
        foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)cache)
        {
            var payloadProp = entry.Value!.GetType().GetProperty("Payload");
            Assert.True(payloadProp != null, "CatalogController.CacheEntry must still expose Payload (this pin's lookup target)");
            string payload = (string)payloadProp!.GetValue(entry.Value)!;
            if (payload.Contains(marker, StringComparison.Ordinal))
            {
                return payload;
            }
        }

        throw new Xunit.Sdk.XunitException($"No stored catalog payload contains the marker '{marker}'");
    }

    /// <summary>
    /// Fake SMAPI backend, the Series fake's shape: catalog creation, catalog
    /// version upload (202 + poll location), poll (SUCCEEDED), skill status,
    /// interaction model GET (static AudiobookTitle seed; later GETs echo the
    /// last PUT body so the canary matches) and PUT.
    /// Per-file by policy (JF-725): the family's full-sync fakes carry their
    /// own mode knobs.
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
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayBookIntent","slots":[{"name":"book","type":"AudiobookTitle"}]}],"types":[{"name":"AudiobookTitle","values":[{"name":{"value":"Il Piccolo Principe"}}]}]}}}
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
                _modelGetCount++;
                if (_modelGetCount > 1 && LastModelPutBody != null)
                {
                    // Post-deploy canary GET: echo what was PUT.
                    return Json(LastModelPutBody);
                }

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
