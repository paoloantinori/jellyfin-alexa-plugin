#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// Song-catalog opt-out teardown tests (JF-949, the JF-946 review F1 defect).
/// The audiobook/song-suite pattern: the FULL sync runs against a fake SMAPI
/// backend through the real CatalogManager. The teardown contract: a disabled
/// CatalogSongSyncEnabled with a stored SongCatalogId strips the SongTitle
/// wiring (restoring the static block), deletes the SMAPI catalog, and clears
/// the id; a failed strip or delete keeps the id for retry; a fresh opt-in
/// after teardown re-syncs from scratch.
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceSongTeardownTests : PluginTestBase, IDisposable
{
    private const string SongCatalogId = "amzn1.catalog.test.teardown-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;

    public LibrarySyncServiceSongTeardownTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _smapiHandler = new FakeSmapiHandler();
        _loggerFactory = LoggerFactory.Create(_ => { });

        var catalogManager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(_smapiHandler)),
            _loggerFactory.CreateLogger<CatalogManager>())
        {
            PollDelayMsForTest = 0
        };

        _service = new LibrarySyncService(
            _libraryManagerMock.Object,
            catalogManager,
            _userDataManagerMock.Object,
            _loggerFactory.CreateLogger<LibrarySyncService>())
        {
            InterLocaleDelayMsForTest = 0
        };

        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-song-teardown-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser(bool songSyncEnabled, string? songCatalogId) => new()
    {
        Id = Guid.NewGuid(),
        InvocationName = "test",
        JellyfinToken = "test-token",
        SmapiDeviceToken = TestHelpers.CreateTestDeviceToken(
            accessToken: "access-token", refreshToken: "refresh-token", expireTimestamp: 9999999999),
        UserSkill = new Entities.UserSkill { SkillId = "amzn1.ask.skill.test-id" },
        VendorId = "test-vendor-id",
        CatalogSongSyncEnabled = songSyncEnabled,
        SongCatalogId = songCatalogId
    };

    /// <summary>
    /// A library holding ONLY songs: with the flag off the reduced table takes
    /// the emptiness skip, so every SMAPI call the run makes is teardown traffic.
    /// </summary>
    private void SetupSongsOnlyLibrary()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.Audio) == true
                ? new List<BaseItem> { new Audio { Name = "Rapsodia", Id = Guid.NewGuid() } }
                : new List<BaseItem>());
    }

    [Fact]
    public async Task OptOut_StripsSongTitleWiring_DeletesCatalog_ClearsId()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // The wiring strip PUT the static SongTitle block back (no valueSupplier).
        string? stripPut = _smapiHandler.ModelPutBodies.FirstOrDefault();
        Assert.NotNull(stripPut);
        var songType = TestHelpers.GetModelTypeNode(stripPut!, "SongTitle");
        Assert.False(songType.TryGetProperty("valueSupplier", out _), "the wired block must lose its catalog supplier");
        Assert.True(songType.TryGetProperty("values", out _), "the static seed block must be restored");
        Assert.Equal("brano", songType.GetProperty("values")[0].GetProperty("name").GetProperty("value").GetString());

        // Exactly one catalog delete, against the user's OWN catalog id.
        Assert.Equal(1, _smapiHandler.CatalogDeleteCount);
        Assert.Contains(SongCatalogId, _smapiHandler.DeletedCatalogIds.Single(), StringComparison.Ordinal);

        // The stored id is cleared after the delete succeeded.
        Assert.Null(user.SongCatalogId);

        // Idempotency of the strip step: the strip PUT is a full model, the
        // ledger row records the source. A songs-only library means this run
        // performed NO other catalog traffic.
        Assert.Equal(0, _smapiHandler.CatalogCreationCount);
    }

    [Fact]
    public async Task OptOut_TeardownIsIdempotent_SecondRunPerformsNoDelete()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        int firstRunDeletes = _smapiHandler.CatalogDeleteCount;

        // Second run: the id is cleared, so the teardown is not due and no
        // delete may fire even though the flag stays off.
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.Equal(1, firstRunDeletes);
        Assert.Equal(1, _smapiHandler.CatalogDeleteCount);
    }

    [Fact]
    public async Task OptOut_NeverOptedIn_PerformsNoTeardown()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: false, songCatalogId: null);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.Null(user.SongCatalogId);
        Assert.Equal(0, _smapiHandler.CatalogDeleteCount);
        Assert.Empty(_smapiHandler.ModelPutBodies);
    }

    [Fact]
    public async Task OptOut_FailedCatalogDelete_KeepsSongCatalogId()
    {
        SetupSongsOnlyLibrary();
        _smapiHandler.DeleteFails = true;
        var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // The strip succeeded (the PUT happened) but the delete failed: the id
        // must stay so the next sync run retries, and the run must not read
        // clean (LastCatalogSync must not advance over a failed teardown).
        Assert.NotNull(_smapiHandler.ModelPutBodies.FirstOrDefault());
        Assert.Equal(SongCatalogId, user.SongCatalogId);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task OptIn_AfterTeardown_ResyncsFromScratch()
    {
        SetupSongsOnlyLibrary();

        // Run 1: opt-in sync mints a catalog and wires the model.
        var user = CreateUser(songSyncEnabled: true, songCatalogId: null);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.Equal(SongCatalogId, user.SongCatalogId);

        // Run 2: opt-out teardown strips the wiring and deletes the catalog.
        user.CatalogSongSyncEnabled = false;
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.Null(user.SongCatalogId);

        // Run 3: opt-in again re-syncs FROM SCRATCH: a fresh catalog version
        // is minted, the model is re-wired with the catalog supplier, and the
        // id lands again.
        user.CatalogSongSyncEnabled = true;
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SongCatalogId, user.SongCatalogId);
        Assert.Equal(2, _smapiHandler.CatalogCreationCount);
        Assert.Equal(1, _smapiHandler.CatalogDeleteCount);

        string lastPut = _smapiHandler.ModelPutBodies.Last();
        var reWired = TestHelpers.GetModelTypeNode(lastPut, "SongTitle");
        Assert.Equal(SongCatalogId, reWired.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    [Fact]
    public async Task StripCatalogWiringAsync_NoWiredBlock_PerformsNoPut()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // The strip PUTs exactly once per sync run (the fake's first model GET
        // serves the WIRED model); a second direct strip against the already-
        // static model must NOT PUT (the JF-949 idempotent no-op).
        int putsBefore = _smapiHandler.ModelPutBodies.Count;
        string? embedded = CatalogSeedEnrichment.GetEmbeddedTypeBlock("it-IT", "SongTitle");
        Assert.NotNull(embedded);
        var catalogManager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(_smapiHandler)),
            _loggerFactory.CreateLogger<CatalogManager>())
        {
            PollDelayMsForTest = 0
        };
        var update = await catalogManager.StripCatalogWiringAsync(
            "access-token", "amzn1.ask.skill.test-id", "development", "it-IT", "SongTitle", embedded!, CancellationToken.None);

        Assert.Equal("Skipped", update.BuildStatus);
        Assert.Equal(putsBefore, _smapiHandler.ModelPutBodies.Count);
    }

    [Fact]
    public void StripCatalogWiring_WiredBlock_IsReplacedWithStatic()
    {
        string wired = """
            {"interactionModel":{"languageModel":{"types":[{"name":"SongTitle","valueSupplier":{"type":"CatalogValueSupplier","valueCatalog":{"catalogId":"c1","version":"2"}}}]}}}
            """;
        string staticBlock = """{"name":"SongTitle","values":[{"name":{"value":"brano"}}]}""";

        (string modified, bool removed) = CatalogManager.StripCatalogWiring(wired, "SongTitle", staticBlock);

        Assert.True(removed);
        using var doc = JsonDocument.Parse(modified);
        var lm = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel");
        var types = lm.GetProperty("types");
        Assert.Equal(1, types.GetArrayLength());
        var node = types[0];
        Assert.False(node.TryGetProperty("valueSupplier", out _));
        Assert.Equal("brano", node.GetProperty("values")[0].GetProperty("name").GetProperty("value").GetString());
    }

    [Fact]
    public void StripCatalogWiring_StaticOrAbsentBlock_IsNoOp()
    {
        string staticModel = """
            {"interactionModel":{"languageModel":{"types":[{"name":"SongTitle","values":[{"name":{"value":"brano"}}]}]}}}
            """;
        string absentModel = """
            {"interactionModel":{"languageModel":{"types":[{"name":"OtherType","values":[]}]}}}
            """;
        string block = """{"name":"SongTitle","values":[{"name":{"value":"brano"}}]}""";

        Assert.False(CatalogManager.StripCatalogWiring(staticModel, "SongTitle", block).Removed);
        Assert.False(CatalogManager.StripCatalogWiring(absentModel, "SongTitle", block).Removed);
    }

    /// <summary>
    /// A locale whose embedded model never carried a SongTitle block passes a null
    /// replacement: the wired block is REMOVED outright (the JF-952 inert-wiring
    /// removal shape), not left in place and not failed (JF-949 review round).
    /// </summary>
    [Fact]
    public void StripCatalogWiring_NullReplacement_RemovesWiredBlockOutright()
    {
        string wired = """
            {"interactionModel":{"languageModel":{"types":[{"name":"SongTitle","valueSupplier":{"type":"CatalogValueSupplier","valueCatalog":{"catalogId":"c1","version":"2"}}}]}}}
            """;

        (string modified, bool removed) = CatalogManager.StripCatalogWiring(wired, "SongTitle", null);

        Assert.True(removed);
        using var doc = JsonDocument.Parse(modified);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        Assert.Equal(0, types.GetArrayLength());
    }

    /// <summary>
    /// A malformed live body is tolerated as unchanged: the strip cannot safely
    /// rewrite a model it cannot parse, and the caller's own failure handling
    /// owns the outcome (JF-949 review round).
    /// </summary>
    [Fact]
    public void StripCatalogWiring_MalformedBody_IsToleratedAsUnchanged()
    {
        (string modified, bool removed) = CatalogManager.StripCatalogWiring("not-json-at-all", "SongTitle", null);

        Assert.False(removed);
        Assert.Equal("not-json-at-all", modified);
    }

    [Fact]
    public void GetEmbeddedTypeBlock_ReturnsEachDeclaringLocalesStaticBlock()
    {
        string? it = CatalogSeedEnrichment.GetEmbeddedTypeBlock("it-IT", "SongTitle");
        Assert.NotNull(it);
        using var itDoc = JsonDocument.Parse(it!);
        Assert.Equal("SongTitle", itDoc.RootElement.GetProperty("name").GetString());
        Assert.True(itDoc.RootElement.TryGetProperty("values", out _));

        string? de = CatalogSeedEnrichment.GetEmbeddedTypeBlock("de-DE", "SongTitle");
        Assert.NotNull(de);
        using var deDoc = JsonDocument.Parse(de!);
        Assert.Equal("titel", deDoc.RootElement.GetProperty("values")[0].GetProperty("name").GetProperty("value").GetString());

        // A locale that does not commit a SongTitle block yields null (the
        // strip cannot fabricate a static block it never shipped).
        Assert.Null(CatalogSeedEnrichment.GetEmbeddedTypeBlock("en-US", "SongTitle"));
    }

    /// <summary>
    /// Fake SMAPI backend: the live model carries the WIRED SongTitle block a
    /// prior opt-in left behind; catalog delete traffic is recorded.
    /// </summary>
    private sealed class FakeSmapiHandler : HttpMessageHandler
    {
        private const string Base = "https://api.amazonalexa.com";
        private const string WiredModelJson = """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayLibrarySongIntent","slots":[{"name":"librarySong","type":"SongTitle"}]}],"types":[{"name":"SongTitle","valueSupplier":{"type":"CatalogValueSupplier","valueCatalog":{"catalogId":"amzn1.catalog.test.teardown-1","version":"1"}}}]}}}
            """;

        public bool DeleteFails { get; set; }

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public int CatalogDeleteCount =>
            Requests.Count(r => r.Method == HttpMethod.Delete && r.Url.Contains("/interactionModel/catalogs/", StringComparison.Ordinal));

        public List<string> DeletedCatalogIds =>
            Requests.Where(r => r.Method == HttpMethod.Delete && r.Url.Contains("/interactionModel/catalogs/", StringComparison.Ordinal))
                .Select(r => r.Url.Split("/catalogs/")[1])
                .ToList();

        public int CatalogCreationCount =>
            Requests.Count(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal));

        public List<string> ModelPutBodies =>
            Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
                .Select(r => r.Body ?? string.Empty)
                .ToList();

        private static string StaticModelJson
        {
            get
            {
                JsonNode? node = JsonNode.Parse(WiredModelJson)!;
                var type = node!["interactionModel"]!["languageModel"]!["types"]![0]!;
                type.AsObject().Remove("valueSupplier");
                type["values"] = new JsonArray { new JsonObject { ["name"] = new JsonObject { ["value"] = "brano" } } };
                return node.ToJsonString();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url, body));

            if (request.Method == HttpMethod.Delete && url.Contains("/interactionModel/catalogs/", StringComparison.Ordinal))
            {
                return DeleteFails
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom", Encoding.UTF8, "text/plain") }
                    : new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal))
            {
                return Json($"{{\"catalogId\":\"{SongCatalogId}\"}}");
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/versions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{SongCatalogId}/updateRequest/req-1") }
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
                string? lastPut = Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
                    .Select(r => r.Body)
                    .LastOrDefault();
                return Json(lastPut ?? WiredModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
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
