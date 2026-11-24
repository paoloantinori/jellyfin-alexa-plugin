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

        // Every strip PUT leaves the SongTitle block unwired: the declaring
        // locales (it-IT, de-DE) get their OWN embedded static block restored,
        // the other roster locales drop the type outright (the JF-952 removal
        // shape). Roster order is irrelevant: assert over the whole PUT set, not
        // a positional body.
        Assert.NotEmpty(_smapiHandler.ModelPutBodies);
        foreach (string putBody in _smapiHandler.ModelPutBodies)
        {
            using var putDoc = JsonDocument.Parse(putBody);
            var putTypes = putDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
            foreach (var typeNode in putTypes.EnumerateArray())
            {
                if (typeNode.TryGetProperty("name", out var nameNode) && nameNode.GetString() == "SongTitle")
                {
                    Assert.False(typeNode.TryGetProperty("valueSupplier", out _), $"a strip PUT still carries the catalog supplier: {putBody}");
                }
            }
        }

        var putBodies = _smapiHandler.ModelPutBodies;
        Assert.Contains(putBodies, b => b.Contains("brano", StringComparison.Ordinal));
        Assert.Contains(putBodies, b => b.Contains("titel", StringComparison.Ordinal));

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
        Assert.NotNull(_smapiHandler.ModelPutBodies.LastOrDefault());
        Assert.Equal(SongCatalogId, user.SongCatalogId);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task OptIn_AfterTeardown_ResyncsFromScratch()
    {
        SetupSongsOnlyLibrary();

        // Run 1: opt-in sync mints a catalog and wires the model. The fake mints
        // a DISTINCT id per creation, so a stale-id reuse cannot masquerade as a
        // fresh sync.
        var user = CreateUser(songSyncEnabled: true, songCatalogId: null);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        string firstId = Assert.Single(_smapiHandler.CreatedCatalogIds);
        Assert.Equal(firstId, user.SongCatalogId);

        // Run 2: opt-out teardown strips the wiring and deletes the catalog.
        user.CatalogSongSyncEnabled = false;
        await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.Null(user.SongCatalogId);

        // Run 3: opt-in again re-syncs FROM SCRATCH: a NEW catalog id is minted
        // (never the deleted one reused), the model is re-wired with that fresh
        // catalog supplier, and the id lands again.
        user.CatalogSongSyncEnabled = true;
        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, _smapiHandler.CatalogCreationCount);
        Assert.Equal(1, _smapiHandler.CatalogDeleteCount);
        string secondId = _smapiHandler.CreatedCatalogIds[1];
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(secondId, user.SongCatalogId);

        string lastPut = _smapiHandler.ModelPutBodies.Last();
        var reWired = TestHelpers.GetModelTypeNode(lastPut, "SongTitle");
        Assert.Equal(secondId, reWired.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    /// <summary>
    /// JF-949 review round finding 1: the strip roster is the FULL wiring-supported
    /// locale set, independent of CatalogSyncLocales. With the sync roster narrowed
    /// to it-IT only, the teardown must still strip de-DE (both models lose the
    /// wired block) BEFORE the catalog delete is allowed, so a locale left out of
    /// the current sync roster can never keep a wired block over a deleted catalog.
    /// </summary>
    [Fact]
    public async Task OptOut_RosterNarrowed_TeardownStillStripsEverySupportedLocale()
    {
        SetupSongsOnlyLibrary();

        // Narrow the sync roster to it-IT only (the opt-in-time roster carried
        // more locales; the sync legs below only see it-IT).
        Plugin.Instance!.Configuration.CatalogSyncLocales = "it-IT";
        try
        {
            var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
            var jellyfinUser = TestHelpers.CreateJellyfinUser();

            var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

            // Both declaring locales stripped: distinct locales in the PUT set.
            var strippedLocales = _smapiHandler.ModelPutLocales.ToHashSet(StringComparer.Ordinal);
            Assert.Contains("it-IT", strippedLocales);
            Assert.Contains("de-DE", strippedLocales);

            // The delete fired only AFTER both strips (a single delete, and the
            // id is cleared, so the all-strips-succeeded gate passed).
            Assert.Equal(1, _smapiHandler.CatalogDeleteCount);
            Assert.Null(user.SongCatalogId);
        }
        finally
        {
            Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
        }
    }

    /// <summary>
    /// JF-949 review round finding 2: a malformed live-model body is a DISTINCT
    /// outcome, not the static case. Every locale's strip reads the malformed body,
    /// so the wiring state is unknown and the catalog delete must NOT fire; the id
    /// stays for retry and the run fails.
    /// </summary>
    [Fact]
    public async Task OptOut_MalformedModelBody_BlocksDeleteAndKeepsId()
    {
        SetupSongsOnlyLibrary();
        _smapiHandler.ServeMalformedModel = true;
        var user = CreateUser(songSyncEnabled: false, songCatalogId: SongCatalogId);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.Equal(0, _smapiHandler.CatalogDeleteCount);
        Assert.Equal(SongCatalogId, user.SongCatalogId);
        Assert.False(result.Success);
        Assert.Empty(_smapiHandler.ModelPutBodies);
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

        (string modified, CatalogManager.CatalogStripOutcome outcome) = CatalogManager.StripCatalogWiring(wired, "SongTitle", staticBlock);

        Assert.Equal(CatalogManager.CatalogStripOutcome.Removed, outcome);
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

        Assert.Equal(CatalogManager.CatalogStripOutcome.Unchanged, CatalogManager.StripCatalogWiring(staticModel, "SongTitle", block).Outcome);
        Assert.Equal(CatalogManager.CatalogStripOutcome.Unchanged, CatalogManager.StripCatalogWiring(absentModel, "SongTitle", block).Outcome);
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

        (string modified, CatalogManager.CatalogStripOutcome outcome) = CatalogManager.StripCatalogWiring(wired, "SongTitle", null);

        Assert.Equal(CatalogManager.CatalogStripOutcome.Removed, outcome);
        using var doc = JsonDocument.Parse(modified);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        Assert.Equal(0, types.GetArrayLength());
    }

    /// <summary>
    /// A malformed live body is the DISTINCT Malformed outcome, never the static
    /// case: the caller must block the catalog delete on it (the wiring state is
    /// unknown), JF-949 review round finding 2.
    /// </summary>
    [Fact]
    public void StripCatalogWiring_MalformedBody_IsDistinctMalformedOutcome()
    {
        (string modified, CatalogManager.CatalogStripOutcome outcome) = CatalogManager.StripCatalogWiring("not-json-at-all", "SongTitle", null);

        Assert.Equal(CatalogManager.CatalogStripOutcome.Malformed, outcome);
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

        /// <summary>
        /// Per-locale model state: each locale's live model carries its OWN wired
        /// block until THIS locale's strip PUT lands (the fake's earlier single
        /// shared body made only the first roster locale ever strip, which hid the
        /// roster bug the full-roster pin exists to catch).
        /// </summary>
        private readonly Dictionary<string, string> _lastPutByLocale = new(StringComparer.Ordinal);

        /// <summary>Skill status carrying EVERY wiring-supported locale as SUCCEEDED, so the no-Location PUT path settles for any roster locale.</summary>
        private readonly string _statusJson;

        public FakeSmapiHandler()
        {
            _statusJson = TestHelpers.SmapiSkillStatusJson(
                Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
                    .Select(m => m.Item1)
                    .Where(CatalogManager.IsCatalogWiringSupported)
                    .ToArray());
        }

        public bool DeleteFails { get; set; }

        /// <summary>JF-949 review round finding 2: serve an unparseable model body on every locales GET.</summary>
        public bool ServeMalformedModel { get; set; }

        private int _creationCounter;

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        /// <summary>JF-949 review round finding 7: a DISTINCT id minted per creation, so a stale-id reuse cannot pass for a fresh sync.</summary>
        public List<string> CreatedCatalogIds { get; } = new();

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

        /// <summary>The locale segment of every model PUT (the roster-narrowing pin reads the set).</summary>
        public List<string> ModelPutLocales =>
            Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
                .Select(r => r.Url.Split("/locales/")[1].Split('/')[0])
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
                string minted = $"amzn1.catalog.test.teardown-{Interlocked.Increment(ref _creationCounter)}";
                CreatedCatalogIds.Add(minted);
                return Json($"{{\"catalogId\":\"{minted}\"}}");
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/versions", StringComparison.Ordinal))
            {
                string targetCatalogId = url.Split("/catalogs/")[1].Split('/')[0];
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{targetCatalogId}/updateRequest/req-1") }
                };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/status", StringComparison.Ordinal))
            {
                return Json(_statusJson);
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/", StringComparison.Ordinal))
            {
                return Json("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"1"}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                if (ServeMalformedModel)
                {
                    return Json("not-json-at-all");
                }

                string locale = url.Split("/locales/")[1].Split('/')[0];
                return Json(_lastPutByLocale.GetValueOrDefault(locale) ?? WiredModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                string putLocale = url.Split("/locales/")[1].Split('/')[0];
                _lastPutByLocale[putLocale] = body ?? string.Empty;
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
