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
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-951 song-catalog build policy tests: the per-user phonetic-synonyms
/// toggle, the pre-flight effective-size guard, and the flag-off byte identity
/// of the shipped values-only default. The estimator and toggle tests are pure;
/// the guard tests run the FULL sync against a fake SMAPI backend (the
/// song-suite pattern) with the threshold seam forcing the over-cap shape.
/// </summary>
public class SongCatalogPolicyUnitTests
{
    [Fact]
    public void EstimateEffectiveModelSize_SumsModelAndPayloads()
    {
        Assert.Equal(6_000, CatalogManager.EstimateEffectiveModelSizeBytes(1_000, new long[] { 2_000, 3_000 }));
    }

    [Fact]
    public void EstimateEffectiveModelSize_NoPayloads_IsModelBytesAlone()
    {
        Assert.Equal(4_321, CatalogManager.EstimateEffectiveModelSizeBytes(4_321, Array.Empty<long>()));
    }

    [Fact]
    public void EstimateEffectiveModelSize_ZeroModel_IsPayloadSum()
    {
        Assert.Equal(700, CatalogManager.EstimateEffectiveModelSizeBytes(0, new long[] { 300, 400 }));
    }

    /// <summary>The two named constants, pinned: the cap and its 1.45MB guard line.</summary>
    [Fact]
    public void GuardConstants_CapAndSafetyMargin_ArePinned()
    {
        Assert.Equal(1_500_000, CatalogManager.ModelSizeCapBytes);
        Assert.Equal(1_450_000, CatalogManager.ModelSizeSafetyThresholdBytes);
        Assert.True(CatalogManager.ModelSizeSafetyThresholdBytes < CatalogManager.ModelSizeCapBytes);
    }

    /// <summary>
    /// The wired-set read (the JF-951 code-review systemic fix, refined by the
    /// merge-review round): the injection's own report is the guard's wired
    /// source of truth. A mapping the JF-952 declare check skipped (the
    /// SongTitle id forwarded but the locale declares no SongTitle slot) is
    /// NOT reported; a wired block of a type whose bytes the run does not know
    /// still reports its CURRENT block bytes (the stale-block approximation,
    /// never zero).
    /// </summary>
    [Fact]
    public void InjectCatalogReferencesDetailed_ReportsWiredBlocksNotForwardedIds()
    {
        // Declaring model (a slot names both types): both blocks wire and are
        // reported with nonzero block bytes.
        string declaringModel = """
            {"interactionModel":{"languageModel":{"intents":[{"name":"I","slots":[{"name":"a","type":"JellyfinArtist"},{"name":"s","type":"SongTitle"}]}],"types":[]}}}
            """;
        var both = CatalogManager.InjectCatalogReferencesDetailed(declaringModel, "a1", null, null, null, null, "1", null, null, null, null, songCatalogId: "s1", songCatalogVersion: "1");
        Assert.Equal(2, both.WiredBlocks.Count);
        Assert.Contains(both.WiredBlocks, w => w.Type == CatalogType.Artist && w.BlockBytes > 0);
        Assert.Contains(both.WiredBlocks, w => w.Type == CatalogType.Song && w.BlockBytes > 0);

        // Undeclared model (no slot names either type): nothing wires, the
        // forwarded ids contribute NOTHING to the report (the JF-952 skip).
        string undeclaredModel = """
            {"interactionModel":{"languageModel":{"intents":[{"name":"I","slots":[]}],"types":[]}}}
            """;
        var none = CatalogManager.InjectCatalogReferencesDetailed(undeclaredModel, "a1", null, null, null, null, "1", null, null, null, null, songCatalogId: "s1", songCatalogVersion: "1");
        Assert.Empty(none.WiredBlocks);

        // A static seed block that the declare check keeps untouched is not
        // a wired block either.
        string staticSeedModel = """
            {"interactionModel":{"languageModel":{"intents":[{"name":"I","slots":[]}],"types":[{"name":"SongTitle","values":[{"name":{"value":"brano"}}]}]}}}
            """;
        var seedKept = CatalogManager.InjectCatalogReferencesDetailed(staticSeedModel, null, null, null, null, null, null, null, null, null, null, songCatalogId: "s1", songCatalogVersion: "1");
        Assert.Empty(seedKept.WiredBlocks);

        // The PUT counts ride the outcome (no second parse).
        Assert.Equal(1, both.PutIntents);
        Assert.Equal(0, both.PutSamples);
    }

    /// <summary>Disabled (the default) three-argument form matches the values-only policy; the two-argument shipped-default pin lives in SongTitlePreparationTests.</summary>
    [Fact]
    public void SongSynonyms_Disabled_IsEmpty()
    {
        Assert.Empty(SongTitlePreparation.SongSynonyms("soul kiss", "it-IT", phoneticSynonymsEnabled: false));
    }

    /// <summary>
    /// Enabled delegates to the locale's phonetic generator byte-for-byte (no
    /// second synonym implementation), and a Romance name actually yields
    /// variants (the "soul" -> "sol" override), so the flow assertion cannot
    /// pass vacuously.
    /// </summary>
    [Fact]
    public void SongSynonyms_Enabled_MatchesPhoneticGenerator_AndYieldsVariants()
    {
        List<string> enabled = SongTitlePreparation.SongSynonyms("Soul Kiss", "it-IT", phoneticSynonymsEnabled: true);
        Assert.Equal(PhoneticSynonymGenerator.GenerateSynonyms("Soul Kiss", "it-IT"), enabled);
        Assert.NotEmpty(enabled);
        Assert.Contains(enabled, s => s.StartsWith("Sol ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Flag-off byte identity: a payload built through the user-controlled
    /// generator with the toggle OFF is byte-identical to the shipped
    /// values-only build (the 2-arg overload). The default sync path must not
    /// change one byte.
    /// </summary>
    [Fact]
    public void FlagOff_Payload_IsByteIdenticalToShippedValuesOnlyBuild()
    {
        var items = new List<(Guid, string)>
        {
            (Guid.NewGuid(), "Soul Kiss"),
            (Guid.NewGuid(), "Rapsodia (Remaster 2011)"),
            (Guid.NewGuid(), "Play Count Anchor")
        };

        CatalogPayload shipped = CatalogPayload.FromItems(CatalogType.Song, items, SongTitlePreparation.SongSynonyms, "it-IT");
        CatalogPayload toggledOff = CatalogPayload.FromItems(
            CatalogType.Song, items, (name, locale) => SongTitlePreparation.SongSynonyms(name, locale, phoneticSynonymsEnabled: false), "it-IT");

        Assert.Equal(
            JsonSerializer.Serialize(shipped, CatalogManager.JsonOptions),
            JsonSerializer.Serialize(toggledOff, CatalogManager.JsonOptions));
    }
}

[Collection("Plugin")]
public class SongCatalogPolicySyncTests : PluginTestBase, IDisposable
{
    private const string SongCatalogId = "amzn1.catalog.test.policy-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly List<(LogLevel Level, string Message)> _logRecords = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CatalogManager _catalogManager;
    private readonly LibrarySyncService _service;

    public SongCatalogPolicySyncTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _smapiHandler = new FakeSmapiHandler(SongCatalogId);
        _loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(_logRecords);

        _catalogManager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(_smapiHandler)),
            _loggerFactory.CreateLogger<CatalogManager>())
        {
            PollDelayMsForTest = 0
        };

        _service = new LibrarySyncService(
            _libraryManagerMock.Object,
            _catalogManager,
            _userDataManagerMock.Object,
            _loggerFactory.CreateLogger<LibrarySyncService>())
        {
            InterLocaleDelayMsForTest = 0
        };

        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-song-policy-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser(bool songSyncEnabled, bool songSynonymsEnabled = false) => new()
    {
        Id = Guid.NewGuid(),
        InvocationName = "test",
        JellyfinToken = "test-token",
        SmapiDeviceToken = TestHelpers.CreateTestDeviceToken(
            accessToken: "access-token", refreshToken: "refresh-token", expireTimestamp: 9999999999),
        UserSkill = new Entities.UserSkill { SkillId = "amzn1.ask.skill.test-id" },
        VendorId = "test-vendor-id",
        CatalogSongSyncEnabled = songSyncEnabled,
        CatalogSongSynonymsEnabled = songSynonymsEnabled
    };

    private void SetupSongsOnlyLibrary()
    {
        var items = new List<BaseItem>
        {
            new Audio { Name = "Soul Kiss", Id = Guid.NewGuid(), DateCreated = DateTime.UtcNow }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.Audio) == true
                ? items
                : new List<BaseItem>());

        _userDataManagerMock
            .Setup(m => m.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "k", PlayCount = 1 });
    }

    /// <summary>
    /// THE GUARD (JF-951): an over-cap locale's model PUT is SKIPPED locally.
    /// The threshold seam (zero) forces the over-cap shape: no model PUT may
    /// reach SMAPI, the locale rides the SyncResult's skipped set, the loud
    /// warning names the guard and the levers, and the run still reads as a
    /// completed sync (the previous good model stays live; the catalogs
    /// themselves uploaded fine).
    /// </summary>
    [Fact]
    public async Task OverCapLocale_PutIsSkipped_ResultCarriesLocale_WarningLogged()
    {
        SetupSongsOnlyLibrary();
        _catalogManager.ModelSizeSafetyThresholdBytesForTest = 0;
        var user = CreateUser(songSyncEnabled: true, songSynonymsEnabled: false);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.Empty(_smapiHandler.ModelPutBodies);
        Assert.Contains("it-IT", result.SkippedOversizeLocales);
        Assert.Single(result.SkippedOversizeLocales);
        Assert.True(result.Success, "a size-guard skip keeps the previous good model; the run must not fail");
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        // ONE warning per skipped locale (the skip is an expected
        // user-configuration outcome, not a failure): it names the guard, the
        // safety threshold and Amazon's cap as separate facts, and the levers.
        Assert.Contains(records, r => r.Level == LogLevel.Warning && r.Message.Contains("JF-951 pre-flight size guard", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Level == LogLevel.Warning && r.Message.Contains("safety threshold", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Message.Contains("sync fewer titles", StringComparison.Ordinal));
        Assert.DoesNotContain(records, r => r.Level == LogLevel.Error && r.Message.Contains("JF-951", StringComparison.Ordinal));

        // The ledger row (fresh ledger: no previous settled status) carries the
        // skip as the durable signal WITHOUT a failing status: clamped to the
        // healthy-neutral Skipped, the OversizeSkipped caveat bit set, the
        // numbers in Error.
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal("Skipped", entry.Status);
        Assert.True(entry.Caveat.HasFlag(Configuration.CatalogLedgerCaveats.OversizeSkipped));
        Assert.Contains("size guard", entry.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LEDGER SEMANTICS (JF-951 merge-review item 2): a skip must NOT
    /// clobber a previous run's SUCCEEDED row. Run 1 (default threshold) PUTs
    /// and records Succeeded; run 2 (seam threshold 0) skips the PUT and the
    /// row must STILL read Succeeded (ModelsDeployed matches only Succeeded),
    /// with the caveat bit carrying the skip signal instead.
    /// </summary>
    [Fact]
    public async Task SkipAfterSuccess_PreservesSucceededLedgerRow()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: true, songSynonymsEnabled: false);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var first = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
        Assert.True(first.Success);
        Assert.Empty(first.SkippedOversizeLocales);
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal("SUCCEEDED", entry.Status);
        Assert.False(entry.Caveat.HasFlag(Configuration.CatalogLedgerCaveats.OversizeSkipped));

        // Second run over the SAME ledger: the guard skips the PUT.
        _catalogManager.ModelSizeSafetyThresholdBytesForTest = 0;
        var second = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.Contains("it-IT", second.SkippedOversizeLocales);
        Assert.True(second.Success);
        var entryAfterSkip = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal("SUCCEEDED", entryAfterSkip.Status);
        Assert.True(entryAfterSkip.Caveat.HasFlag(Configuration.CatalogLedgerCaveats.OversizeSkipped));
        Assert.Contains("size guard", entryAfterSkip.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under the default threshold a small library PUTs normally: the guard is
    /// invisible, no locale is reported skipped.
    /// </summary>
    [Fact]
    public async Task UnderCapLocale_NormalPut_NoSkippedLocales()
    {
        SetupSongsOnlyLibrary();
        var user = CreateUser(songSyncEnabled: true, songSynonymsEnabled: false);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(_smapiHandler.ModelPutBodies);
        Assert.Empty(result.SkippedOversizeLocales);
    }

    /// <summary>
    /// THE TOGGLE (JF-951) end to end: with the phonetic toggle ON, the song
    /// catalog payload SMAPI would fetch carries the locale's phonetic
    /// synonyms per title (exactly what SongSynonyms(enabled) produces);
    /// with it OFF the same library ships the values-only payload with empty
    /// synonyms (the shipped default, unchanged).
    /// </summary>
    [Fact]
    public async Task ToggleOn_PayloadCarriesPhoneticSynonyms_ToggleOff_ShipsValuesOnly()
    {
        SetupSongsOnlyLibrary();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var onUser = CreateUser(songSyncEnabled: true, songSynonymsEnabled: true);
        await _service.SyncUserLibraryAsync(onUser, jellyfinUser, CancellationToken.None);
        string onPayload = ReadLastUploadedPayload();
        List<string> onSynonyms = ReadSynonymsOf(onPayload, "Soul Kiss");
        Assert.Equal(SongTitlePreparation.SongSynonyms("Soul Kiss", "it-IT", phoneticSynonymsEnabled: true), onSynonyms);
        Assert.NotEmpty(onSynonyms);

        // The OFF run must ship the values-only payload (empty synonyms), the
        // shipped default byte-shape; the read resolves the run's OWN uploaded
        // payload via its version-request source URL, so the earlier ON payload
        // lingering in the cache cannot be mistaken for it.
        var offUser = CreateUser(songSyncEnabled: true, songSynonymsEnabled: false);
        await _service.SyncUserLibraryAsync(offUser, jellyfinUser, CancellationToken.None);
        string offPayload = ReadLastUploadedPayload();
        List<string> offSynonyms = ReadSynonymsOf(offPayload, "Soul Kiss");
        Assert.Empty(offSynonyms);
    }

    /// <summary>
    /// The payload of the run's LAST catalog version upload: the version POST
    /// body carries the source URL with the CatalogController cache key, so the
    /// lookup follows the actual upload instead of guessing from cache contents.
    /// </summary>
    private string ReadLastUploadedPayload()
    {
        var cacheField = typeof(CatalogController).GetField("CatalogCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(cacheField != null, "CatalogController.CatalogCache must still exist (this pin's lookup target)");
        var cache = (System.Collections.IDictionary)cacheField!.GetValue(null)!;

        (HttpMethod, string Url, string? Body) lastVersion = _smapiHandler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/versions", StringComparison.Ordinal))
            .Last();
        string body = lastVersion.Body ?? throw new Xunit.Sdk.XunitException("The last version upload carried no body");
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            body, @"alexaskill/catalog/([0-9a-f]{32})", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, $"The version body carries no catalog source URL: {body}");
        string key = match.Groups[1].Value;
        Assert.True(cache.Contains(key), $"The cache key {key} the last upload referenced is no longer stored");
        var entry = cache[key]!;
        return (string)entry.GetType().GetProperty("Payload")!.GetValue(entry)!;
    }

    private static List<string> ReadSynonymsOf(string payloadJson, string value)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        foreach (var entry in doc.RootElement.GetProperty("values").EnumerateArray())
        {
            if (entry.GetProperty("name").GetProperty("value").GetString() == value)
            {
                // The values-only payload omits the synonyms element entirely
                // (null under CatalogManager.JsonOptions) or carries it null:
                // both read as the empty shipped shape.
                if (!entry.GetProperty("name").TryGetProperty("synonyms", out var synonyms) || synonyms.ValueKind != JsonValueKind.Array)
                {
                    return new List<string>();
                }

                return synonyms.EnumerateArray().Select(s => s.GetString()!).ToList();
            }
        }

        throw new Xunit.Sdk.XunitException($"Payload has no value '{value}'");
    }

    /// <summary>
    /// Fake SMAPI backend, the song-suite fake's shape: the static SongTitle
    /// seed model, catalog create/version/status traffic recorded.
    /// </summary>
    private sealed class FakeSmapiHandler : HttpMessageHandler
    {
        private readonly string _catalogId;
        private const string Base = "https://api.amazonalexa.com";

        public FakeSmapiHandler(string catalogId)
        {
            _catalogId = catalogId;
        }

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public int CatalogCreationCount =>
            Requests.Count(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/interactionModel/catalogs", StringComparison.Ordinal));

        public List<string> ModelPutBodies =>
            Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
                .Select(r => r.Body ?? string.Empty)
                .ToList();

        private static string ModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayLibrarySongIntent","slots":[{"name":"librarySong","type":"SongTitle"}]}],"types":[{"name":"SongTitle","values":[{"name":{"value":"brano"}}]}]}}}
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
                return Json(ModelJson);
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
