#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// JF-717 equivalence-class pins. The uploaded catalog payload depends on the
/// locale only through the LocalePrefix-keyed synonym generator, so the
/// byte-identical locale classes (es x3, fr x2, the no-generator en/hi cluster)
/// share one catalog upload per run. The pre-JF-717 JF-513.3 skip returned a
/// null version for every later class member, the injection gate read the leg
/// as nothing-to-wire, and those locales' models NEVER received catalog
/// references (catalog ER dead there; 8 of 16 synced locales under the
/// default "*" config). These pins drive the full SyncUserLibraryAsync against
/// a fake SMAPI backend (the LibrarySyncServiceLegIsolationTests pattern) with
/// two members of the real es class (es-MX + es-US: same "es" prefix, so the
/// payload generator provably produces identical bytes) and pin that BOTH
/// locales' models get the catalog-wired PUT, the shared version threaded is
/// the one the class's first member minted THIS run, the version-upload skip
/// still holds (the JF-513.3 quota goal: one upload per byte-identical payload
/// per run, not per locale), and the outcome is independent of which class
/// member runs first. The "*" config itself resolves its locale list through
/// the Alexa.NET.Management manifest GET (no test seam), so the pins use
/// explicit comma configs; the starvation mechanism lives downstream of the
/// list resolution, entirely inside the leg loop pinned here, and
/// ResolveSyncLocalesAsync's list production is pinned separately by
/// LibrarySyncServiceLocaleTests.
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceEquivalenceClassTests : PluginTestBase, IDisposable
{
    private const string ArtistCatalogId = "amzn1.catalog.test.artist-1";
    private const string Base = "https://api.amazonalexa.com";

    /// <summary>
    /// Discriminates the it-IT payload from the es-class payload
    /// deterministically: ItalianPhoneticSynonyms transforms "Faith" via
    /// th-&gt;t and adds the "i " article variant, SpanishPhoneticSynonyms
    /// produces th-&gt;d and "los " forms, so the two payloads' bytes differ
    /// while es-MX and es-US (same "es" prefix dispatch) stay identical.
    /// </summary>
    private const string ArtistName = "Faith No More";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;
    private readonly List<(LogLevel Level, string Message)> _logCapture;
    private readonly string _originalCatalogSyncLocales;

    public LibrarySyncServiceEquivalenceClassTests()
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
            _loggerFactory.CreateLogger<LibrarySyncService>())
        {
            // The fake backend needs no rate limiting; the multi-locale pins
            // would otherwise sleep 2s per leg boundary (JF-717 seam).
            InterLocaleDelayMsForTest = 0
        };

        // SyncCatalogForLocaleAsync reads Plugin.Instance.Configuration.ServerAddress
        // when building the hosted catalog URL.
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-equivalence-class-test");

        // Hermeticity (the LegIsolationTests pattern): pin the locale set so the
        // suite never leaves the fake; each test overrides within try/finally.
        _originalCatalogSyncLocales = Plugin.Instance!.Configuration.CatalogSyncLocales;
        Plugin.Instance.Configuration.CatalogSyncLocales = string.Empty;
    }

    public void Dispose()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.CatalogSyncLocales = _originalCatalogSyncLocales;
        }

        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser() => TestHelpers.CreateSyncUser();

    private void SetupLibraryWithArtist()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?[0] == BaseItemKind.MusicArtist
                ? new List<BaseItem> { new MusicArtist { Name = ArtistName, Id = Guid.NewGuid() } }
                : new List<BaseItem>());
    }

    /// <summary>
    /// Extracts the pinned (catalogId, version) pair from a model PUT body's
    /// catalog-backed JellyfinArtist type definition.
    /// </summary>
    private static (string CatalogId, string Version) GetArtistCatalogPinning(string modelJson)
    {
        var catalog = TestHelpers.GetModelTypeNode(modelJson, "JellyfinArtist")
            .GetProperty("valueSupplier")
            .GetProperty("valueCatalog");
        return (catalog.GetProperty("catalogId").GetString()!, catalog.GetProperty("version").GetString()!);
    }

    /// <summary>
    /// CORE PIN: under a multi-locale run covering two members of one
    /// byte-identical class (it-IT seeded first, then es-MX, then es-US), the
    /// first es leg uploads its own version and the second es leg SKIPS the
    /// upload but still receives its model PUT wired with the class's SHARED
    /// (catalogId, version): es-MX's fresh mint, not it-IT's different-payload
    /// version and not a null/fallback pin. The newly-wired locale writes the
    /// normal JF-705 ledger row. The version-upload skip keeps the JF-513.3
    /// quota goal: exactly one upload per distinct payload (it + es), not one
    /// per locale.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SameClassSecondLocale_GetsModelPutWiredWithSharedVersion()
    {
        SetupLibraryWithArtist();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        Plugin.Instance!.Configuration.CatalogSyncLocales = "es-MX,es-US";

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);

        // JF-513.3 quota goal kept: it-IT's Italian payload + ONE es mint;
        // es-US skipped the upload (the pre-fix shape also skipped it, but
        // then never PUT its model).
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.Contains(_logCapture, l => l.Level == LogLevel.Information
            && l.Message.Contains("skipping the version upload and wiring the shared catalog version", StringComparison.Ordinal)
            && l.Message.Contains("es-US", StringComparison.Ordinal));

        // ALL THREE locales' models were PUT (the starved locale included).
        Assert.NotNull(_smapiHandler.ModelPutBodyFor("it-IT"));
        Assert.NotNull(_smapiHandler.ModelPutBodyFor("es-MX"));
        Assert.NotNull(_smapiHandler.ModelPutBodyFor("es-US"));

        // First-member behavior unchanged: each minting leg pins its own
        // fresh version (per-catalog counters: it-IT minted v1, es-MX v2).
        var (itCatalog, itVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("it-IT")!);
        var (mxCatalog, mxVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("es-MX")!);
        Assert.Equal(ArtistCatalogId, itCatalog);
        Assert.Equal(ArtistCatalogId, mxCatalog);
        Assert.Equal("1", itVersion);
        Assert.Equal("2", mxVersion);

        // THE FIX: the hash-skipped locale pins the class's shared pair -
        // es-MX's fresh mint ("2", not it-IT's "1"), same catalog id.
        var (usCatalog, usVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("es-US")!);
        Assert.Equal(ArtistCatalogId, usCatalog);
        Assert.Equal(mxVersion, usVersion);

        // The newly-wired locale writes the normal JF-705 ledger row.
        var ledger = Plugin.Instance!.Configuration.GetLocaleModelStatus("es-US");
        Assert.NotNull(ledger);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, ledger!.Source);
        Assert.Equal("SUCCEEDED", ledger.Status);
        Assert.Null(ledger.Error);
    }

    /// <summary>
    /// ORDER INDEPENDENCE (DoD): with the class members swapped (es-US runs
    /// first), es-US is now the minting leg and es-MX the hash-skipped one;
    /// both locales still end wired, both pin the SAME version (es-US's fresh
    /// mint), and the upload count is unchanged (it + one es mint).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SameClassReversedOrder_BothLocalesStillWired()
    {
        SetupLibraryWithArtist();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        Plugin.Instance!.Configuration.CatalogSyncLocales = "es-US,es-MX";

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(ArtistCatalogId));

        Assert.NotNull(_smapiHandler.ModelPutBodyFor("es-US"));
        Assert.NotNull(_smapiHandler.ModelPutBodyFor("es-MX"));

        // es-US minted (v2, after it-IT's v1); es-MX skipped and pins the
        // SAME shared version; whichever member runs first, both wire.
        var (usCatalog, usVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("es-US")!);
        var (mxCatalog, mxVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("es-MX")!);
        Assert.Equal(ArtistCatalogId, usCatalog);
        Assert.Equal(ArtistCatalogId, mxCatalog);
        Assert.Equal("2", usVersion);
        Assert.Equal(usVersion, mxVersion);
    }

    /// <summary>
    /// A locale whose payload is UNIQUE in the run (de-DE: German synonyms,
    /// a class of one alongside it-IT) is unaffected by the shared-version
    /// wiring: it uploads its own version, its model PUT pins its OWN fresh
    /// mint, and the skip never fires. This is the "narrow-config locale
    /// unaffected" boundary: the fix must only change the byte-identical
    /// class members' behavior.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_UniquePayloadLocale_UploadsAndPinsItsOwnVersion()
    {
        SetupLibraryWithArtist();
        var user = CreateUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        Plugin.Instance!.Configuration.CatalogSyncLocales = "de-DE";

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);

        // Two distinct payloads, two uploads; no skip fired anywhere.
        Assert.Equal(2, _smapiHandler.VersionUploadsFor(ArtistCatalogId));
        Assert.DoesNotContain(_logCapture, l => l.Message.Contains("skipping the version upload", StringComparison.Ordinal));

        Assert.NotNull(_smapiHandler.ModelPutBodyFor("it-IT"));
        Assert.NotNull(_smapiHandler.ModelPutBodyFor("de-DE"));

        var (_, itVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("it-IT")!);
        var (deCatalog, deVersion) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("de-DE")!);
        Assert.Equal(ArtistCatalogId, deCatalog);
        Assert.Equal("1", itVersion);
        Assert.Equal("2", deVersion);
    }

    /// <summary>
    /// JF-717 review pin: the 401-refresh-retry x memo interplay. When a leg
    /// 401s AFTER its upload already minted (here the model PUT dies, not the
    /// upload), the retry's second attempt memo-hits the attempt-1 mint, so
    /// the type contributes the run's minted version and the retried PUT
    /// fires wired. The pre-JF-717 skip returned null for that type, so the
    /// retry produced an EMPTY minted table, no PUT, and the locale read as a
    /// clean success while its model stayed unwired: a second starvation
    /// shape, fixed silently by the shared-version change and pinned here so
    /// it cannot regress unnoticed. The JF-703 addendum pin
    /// (LibrarySyncServiceLegIsolationTests) covers the OTHER side: the upload
    /// itself 401s before any mint, so the memo must NOT hit and the type
    /// re-uploads.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_MidLeg401AfterUpload_RetryPutsFromMemoHit()
    {
        SetupLibraryWithArtist();
        _smapiHandler.FailFirstModelPutWith401 = true;
        var user = CreateUser();
        user.SmapiRefreshToken = "refresh-token";
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // The 401 retry refreshes via LWA; serve it locally (the JF-703 pin
        // pattern). The override and credentials are static/global, so they
        // are restored in a finally (unlike the per-test locale config, which
        // the class Dispose already covers).
        Plugin.Instance!.Configuration.LwaClientId = "test-client-id";
        Plugin.Instance!.Configuration.LwaClientSecret = "test-client-secret";
        LwaClient.HttpClientOverrideForTests = () => new HttpClient(new Lwa.SmapiTokenRefresherTests.HappyHandler());
        try
        {
            var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

            Assert.True(result.Success);

            // The artist version uploaded ONCE (attempt 1, before the 401);
            // the retry memo-hit it instead of re-minting.
            Assert.Equal(1, _smapiHandler.VersionUploadsFor(ArtistCatalogId));

            // The model PUT was attempted twice (401, then success) and the
            // retried PUT wires the run's mint.
            Assert.Equal(2, _smapiHandler.ModelPutRequestsFor("it-IT"));
            var (catalog, version) = GetArtistCatalogPinning(_smapiHandler.ModelPutBodyFor("it-IT")!);
            Assert.Equal(ArtistCatalogId, catalog);
            Assert.Equal("1", version);
        }
        finally
        {
            LwaClient.HttpClientOverrideForTests = null;
            Plugin.Instance!.Configuration.LwaClientId = string.Empty;
            Plugin.Instance!.Configuration.LwaClientSecret = string.Empty;
        }
    }

    /// <summary>
    /// Fake SMAPI backend serving the multi-locale surface the pins need:
    /// per-catalog INCREMENTING versions (so it-IT's mint is distinguishable
    /// from the es class's), per-locale model PUT capture, and a skill status
    /// that reports SUCCEEDED for every locale the tests run (a locale absent
    /// from the status map makes the post-PUT fallback tracker poll its full
    /// budget, hanging the pin for minutes). Per-file by policy (JF-725): mode
    /// knobs diverge across the family's full-sync fakes; the hoist boundary and
    /// the absent-locale hazard live on TestHelpers.SmapiSkillStatusJson (the
    /// mode-knob enumeration sits on the LegIsolation fake).
    /// </summary>
    private sealed class FakeSmapiHandler : HttpMessageHandler
    {
        /// <summary>Locales this fake answers SUCCEEDED in the per-skill status map; extend in a pin's arrange when syncing another locale.</summary>
            internal HashSet<string> ServedStatusLocales { get; } = new(StringComparer.Ordinal)
            { "it-IT", "es-MX", "es-US", "de-DE" };

            private readonly Dictionary<string, int> _versionCounterByCatalog = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _modelPutBodiesByLocale = new(StringComparer.Ordinal);
        private bool _modelPut401Served;

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        /// <summary>
        /// JF-717 review pin: when true, the FIRST model PUT answers 401 (the
        /// leg-level refresh-retry then re-runs the leg; the memo must supply
        /// the already-minted version).
        /// </summary>
        public bool FailFirstModelPutWith401 { get; set; }

        public int VersionUploadsFor(string catalogId) =>
            Requests.Count(r => r.Method == HttpMethod.Post
                && r.Url == $"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/versions");

        public int ModelPutRequestsFor(string locale) =>
            Requests.Count(r => r.Method == HttpMethod.Put && r.Url.EndsWith($"/locales/{locale}", StringComparison.Ordinal));

        public string? ModelPutBodyFor(string locale) =>
            _modelPutBodiesByLocale.TryGetValue(locale, out string? body) ? body : null;

        private static string ModelJson =>
            """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayArtistSongsIntent","slots":[{"name":"musician","type":"JELLYFIN_ARTIST"}]}],"types":[{"name":"JellyfinArtist","values":[{"name":{"value":"Mina"}}]}]}}}
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
                return Json($"{{\"catalogId\":\"{ArtistCatalogId}\"}}");
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/versions", StringComparison.Ordinal))
            {
                string catalogId = url.Split('/')[^2];
                int version = _versionCounterByCatalog.TryGetValue(catalogId, out int current) ? current + 1 : 1;
                _versionCounterByCatalog[catalogId] = version;

                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/{catalogId}/updateRequest/req-1") }
                };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/status", StringComparison.Ordinal))
            {
                // Gate-marker tail: the map is driven by ServedStatusLocales (a
                // mutable set, defaulting to the four locales this class's pins
                // sync) so a future pin naming another locale adds it to the set
                // in its arrange instead of silently burning the fallback
                // tracker's full poll budget per unserved locale (the absent-
                // locale hazard is documented on TestHelpers.SmapiSkillStatusJson).
                return Json(TestHelpers.SmapiSkillStatusJson(ServedStatusLocales.ToArray()));
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/", StringComparison.Ordinal))
            {
                string catalogId = url.Split('/')[^3];
                int version = _versionCounterByCatalog.TryGetValue(catalogId, out int current) ? current : 1;
                return Json($"{{\"lastUpdateRequest\":{{\"status\":\"SUCCEEDED\",\"version\":\"{version}\"}}}}");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                return Json(ModelJson);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                if (FailFirstModelPutWith401 && !_modelPut401Served)
                {
                    _modelPut401Served = true;
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"message\":\"Token is invalid/expired.\"}", Encoding.UTF8, "application/json")
                    };
                }

                string locale = url.Split('/')[^1];
                _modelPutBodiesByLocale[locale] = body ?? string.Empty;
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
