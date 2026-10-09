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
/// JF-825 cap pins: <see cref="LibrarySyncService.MaxCatalogValues"/> must bind
/// the FINAL catalog payload (library values + appended seed titles + the
/// locale's generic word, after the same-name dedup), not just the fetch. The
/// pre-fix code fetched with Limit=cap but appended the seed values AFTER the
/// bounded fetch and only LOGGED "Truncated ... to {Limit} items" while the
/// payload sailed past the cap untouched: SMAPI received the oversized payload
/// and the log asserted a truncation that never happened. These pins run the
/// full sync against the fake SMAPI backend with the
/// <see cref="LibrarySyncService.MaxCatalogValuesForTest"/> seam set to a small
/// cap, so the tail-cut semantics (the generic word and seed titles sit at the
/// tail and drop first, then the library tail in reverse fetch order) and the
/// warning's honesty (fires exactly when something was dropped, naming what)
/// are both observable without materializing fifty thousand items.
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceCatalogCapTests : PluginTestBase, IDisposable
{
    private const string AudiobookCatalogId = "amzn1.catalog.test.cap-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;
    private readonly List<(LogLevel Level, string Message)> _logCapture;
    private readonly List<InternalItemsQuery> _capturedQueries;

    public LibrarySyncServiceCatalogCapTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _smapiHandler = new FakeSmapiHandler(AudiobookCatalogId);
        _logCapture = new List<(LogLevel Level, string Message)>();
        _capturedQueries = new List<InternalItemsQuery>();
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
            "alexa-catalog-cap-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Only the AudioBook query returns items; the other three types return
    /// empty, so all observed SMAPI traffic is audiobook-catalog traffic. The
    /// query capture pins that the FETCH respects the same cap the payload
    /// truncation enforces (the seam governs both sites). Second private copy
    /// of the audiobook suite's helper (this one adds the query capture);
    /// hoist together with ReadStoredPayloadContaining on the third copy.
    /// </summary>
    private void SetupLibraryWithAudiobooks(params string[] bookNames)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                _capturedQueries.Add(q);
                return q.IncludeItemTypes?.Contains(BaseItemKind.AudioBook) == true
                    ? bookNames.Select(n => (BaseItem)new AudioBook { Name = n, Id = Guid.NewGuid() }).ToList()
                    : new List<BaseItem>();
            });
    }

    /// <summary>
    /// THE TAIL-CUT PIN: a library whose values alone exceed the cap keeps the
    /// priority head (the first library items in fetch order) and drops the
    /// generic word and ALL seed values plus the library tail. Payload order IS
    /// the priority order (library items in fetch order, then static seeds,
    /// generic word last), so a tail cut cannot drop a seed while a library
    /// value survives beyond the cut line, and cannot drop an earlier-fetched
    /// library item while a later-fetched one survives.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_LibraryAloneExceedsCap_TruncatesTailAndNamesDropped()
    {
        const int Cap = 5;
        string[] books = { "Cap Book A", "Cap Book B", "Cap Book C", "Cap Book D", "Cap Book E", "Cap Book F", "Cap Book G", "Cap Book H" };
        _service.MaxCatalogValuesForTest = Cap;
        SetupLibraryWithAudiobooks(books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        // The stored payload (what SMAPI fetches) is capped at exactly the limit.
        string payload = ReadStoredPayloadContaining("Cap Book A");
        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(Cap, values.GetArrayLength());

        // The kept entries are the priority head: the first five library books
        // in fetch order (SortName asc), never seeds.
        for (int i = 0; i < Cap; i++)
        {
            Assert.Equal(books[i], values[i].GetProperty("name").GetProperty("value").GetString());
        }

        // The fetch respected the SAME cap (the seam governs both sites).
        Assert.All(_capturedQueries, q => Assert.Equal(Cap, q.Limit));

        // The count the sync reports stays the FETCH count (8 books found); the
        // truncation warning carries the honest upload numbers instead.
        Assert.Equal(books.Length, result.AudiobookCount);

        // The warning fires because entries were dropped, and NAMES them: the
        // first dropped entries are the library tail (Cap Book F..H) followed
        // by the seed titles, so the sample must carry both categories.
        string warning = SingleTruncationWarning();
        int droppedTotal = books.Length - Cap + CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        Assert.Contains($"dropped {droppedTotal}", warning, StringComparison.Ordinal);
        Assert.Contains("Cap Book F", warning, StringComparison.Ordinal);
        Assert.Contains(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook)[0],
            warning,
            StringComparison.Ordinal);

        // JF-825 code-review F2: the fetch that saturated the cap keeps its own
        // signal even though the payload truncation covers this case too (a
        // seedless type like Series drops nothing at saturation and would
        // otherwise log nothing at all).
        Assert.Contains(Logs(), l => l.Contains("fetch reached the", StringComparison.Ordinal));
    }

    /// <summary>
    /// With room left under the cap after the library values, the seeds fill
    /// the remaining slots (library wins over seeds, the JF-541 principle) and
    /// only the seed tail drops: no library value is lost while a seed
    /// survives.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_CapLeavesSeedRoom_KeepsAllLibraryThenSeedHead()
    {
        string[] books = { "Cap Room Book A", "Cap Room Book B", "Cap Room Book C", "Cap Room Book D" };
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        int cap = books.Length + 2;
        _service.MaxCatalogValuesForTest = cap;
        SetupLibraryWithAudiobooks(books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Cap Room Book A");
        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(cap, values.GetArrayLength());

        // Every library book survives ...
        var names = values.EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();
        Assert.Equal(books, names.Take(books.Length));

        // ... then the FIRST seeds in append order fill the rest; the seed tail
        // beyond the cap is what dropped.
        Assert.Equal(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Take(cap - books.Length),
            names.Skip(books.Length));

        string warning = SingleTruncationWarning();
        Assert.Contains($"dropped {seedCount - (cap - books.Length)}", warning, StringComparison.Ordinal);

        // The library did not saturate the fetch (4 items < 6 cap), so the
        // fetch-saturation signal must stay quiet here.
        Assert.DoesNotContain(Logs(), l => l.Contains("fetch reached the", StringComparison.Ordinal));
    }

    /// <summary>
    /// The boundary pin: a payload landing EXACTLY at the cap dropped nothing,
    /// so no truncation warning may fire. The pre-fix code warned at
    /// count &gt;= cap, claiming a truncation of a payload that was never
    /// truncated even at the boundary.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_PayloadExactlyAtCap_NoWarningNoTruncation()
    {
        string[] books = { "Boundary Book A", "Boundary Book B" };
        int cap = books.Length + CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        _service.MaxCatalogValuesForTest = cap;
        SetupLibraryWithAudiobooks(books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Boundary Book A");
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(cap, doc.RootElement.GetProperty("values").GetArrayLength());

        Assert.Empty(TruncationWarnings());
    }

    private List<string> Logs() => TestCaptureLogger.Snapshot(_logCapture).Select(l => l.Message).ToList();

    private List<string> TruncationWarnings() =>
        Logs().Where(l => l.Contains("Truncated", StringComparison.Ordinal)).ToList();

    private string SingleTruncationWarning()
    {
        List<string> warnings = TruncationWarnings();
        Assert.True(warnings.Count > 0, $"no truncation warning captured; logs: {string.Join(" | ", Logs())}");
        Assert.Single(warnings);
        return warnings[0];
    }

    /// <summary>
    /// Reads the catalog payload the sync stored for SMAPI to fetch (the
    /// CatalogController cache), selecting the entry that contains the given
    /// marker. The second private copy of the audiobook suite's helper (hoist
    /// to TestHelpers on the third, the GetModelTypeNode convention).
    /// </summary>
    private static string ReadStoredPayloadContaining(string marker)
    {
        var controllerType = typeof(CatalogController);
        var cacheField = controllerType.GetField("CatalogCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(cacheField != null, "CatalogController.CatalogCache must still exist (this pin's lookup target)");
        var cache = cacheField!.GetValue(null)!;

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
    /// Fake SMAPI backend, the audiobook suite fake's shape (per-file by
    /// policy, JF-725): catalog creation, catalog version upload (202 + poll
    /// location), poll (SUCCEEDED), skill status, interaction model GET (later
    /// GETs echo the last PUT body so the canary matches) and PUT.
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

        public string? LastModelPutBody { get; private set; }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
