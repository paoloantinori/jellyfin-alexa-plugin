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
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-825 + JF-848/JF-879 cap pins: <see cref="LibrarySyncService.MaxCatalogValues"/>
/// must bind the FINAL catalog payload (library values + the appended static
/// tail, after the same-name dedup), not just the fetch; the cut itself is
/// <see cref="CatalogPayload.TruncateTo"/>'s reserve-on-any-cut partition
/// (canonical rationale there). These pins run the full sync against the
/// fake SMAPI backend with the
/// <see cref="LibrarySyncService.MaxCatalogValuesForTest"/> seam set to a
/// small cap, so both regimes and the warning's honesty (fires exactly when
/// something was dropped, naming what) are observable without materializing
/// fifty thousand items.
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
    /// Only the given kind's query returns items; the other types return
    /// empty, so all observed SMAPI traffic is that type's catalog traffic.
    /// The query capture pins that the FETCH respects the same cap the
    /// payload truncation enforces (the seam governs both sites). Second
    /// private copy of the audiobook suite's helper (this one adds the query
    /// capture and the JF-848 kind parameter); hoist together with
    /// ReadStoredPayloadContaining on the third copy.
    /// </summary>
    private void SetupLibraryWith(BaseItemKind kind, params string[] names)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                _capturedQueries.Add(q);
                return q.IncludeItemTypes?.Contains(kind) == true
                    ? names.Select(n => (BaseItem)CreateItem(kind, n)).ToList()
                    : new List<BaseItem>();
            });
    }

    private static BaseItem CreateItem(BaseItemKind kind, string name) => kind switch
    {
        BaseItemKind.AudioBook => new AudioBook { Name = name, Id = Guid.NewGuid() },
        BaseItemKind.Series => new MediaBrowser.Controller.Entities.TV.Series { Name = name, Id = Guid.NewGuid() },
        BaseItemKind.Movie => TestHelpers.CreateMovie(name),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no test item shape for this kind")
    };

    /// <summary>
    /// THE JF-848 SATURATION PIN (seeded type, the production shape: the
    /// library block lands exactly AT the cap because the fetch Limit equals
    /// it): the static tail (the seed titles + the locale's generic word) is
    /// reserved FIRST and survives whole, while the library head is cut to
    /// (cap − staticCount). The trade-off is pinned with it: the library
    /// keeps only (cap − staticCount) of its own values, and the drop
    /// warning names the library tail as what dropped (policy rationale:
    /// <see cref="CatalogPayload.TruncateTo"/>).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_LibraryFillsCap_ReservesStaticSeedSlots()
    {
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        // Enough distinct books to fill the whole cap; none collides with a seed.
        string[] books = Enumerable.Range(1, 25).Select(i => $"Reserved Book {i:D2}").ToArray();
        int cap = books.Length;
        _service.MaxCatalogValuesForTest = cap;
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Reserved Book 01");
        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(cap, values.GetArrayLength());

        var names = values.EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();

        // The library head keeps exactly (cap - staticCount) slots ...
        Assert.Equal(books.Take(cap - seedCount), names.Take(cap - seedCount));

        // ... and the ENTIRE static seed floor survives after it (it-IT rides
        // no generic word; the seed block is the whole reserved tail).
        Assert.Equal(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook),
            names.Skip(cap - seedCount));

        // The fetch respected the SAME cap (the seam governs both sites).
        Assert.All(_capturedQueries, q => Assert.Equal(cap, q.Limit));

        // The count the sync reports stays the FETCH count (25 books found); the
        // truncation warning carries the honest upload numbers instead.
        Assert.Equal(books.Length, result.AudiobookCount);

        // The warning fires because entries were dropped and NAMES them: at
        // saturation the whole drop is the library tail (0 static entries
        // dropped), and the sample leads with the first dropped book.
        string warning = SingleTruncationWarning();
        int expectedDropped = books.Length - (cap - seedCount);
        Assert.Contains(ExpectedDropComposition(expectedDropped, 0), warning, StringComparison.Ordinal);

        // The dropped sample leads with the first book past the reserved
        // library head; DERIVED from the live seed count so a template seed
        // edit shifts the expectation with it instead of failing blindly.
        Assert.Contains($"Reserved Book {cap - seedCount + 1:D2}", warning, StringComparison.Ordinal);

        // JF-825 code-review F2: the fetch that saturated the cap keeps its own
        // signal even though the payload truncation now reserves the static
        // floor instead of covering this case with a static drop.
        Assert.Contains(Logs(), l => l.Contains("fetch reached the", StringComparison.Ordinal));
    }

    /// <summary>
    /// The seam-only extreme of the reservation: a cap BELOW the static block's
    /// own size. The static floor outranks the library entirely (reserved
    /// FIRST), so the payload is the static head alone, the library drops
    /// whole, and the static tail beyond the cap drops after it so the cap
    /// still binds. Production never sees this (static counts are tens against
    /// a 50000 cap); the pin keeps the degenerate shape honest.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_CapBelowStaticBlock_KeepsStaticHeadOnly()
    {
        const int Cap = 5;
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        string[] books = { "Tiny Cap Book A", "Tiny Cap Book B", "Tiny Cap Book C", "Tiny Cap Book D", "Tiny Cap Book E", "Tiny Cap Book F", "Tiny Cap Book G", "Tiny Cap Book H" };
        _service.MaxCatalogValuesForTest = Cap;
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook)[0]);
        using var doc = JsonDocument.Parse(payload);
        var names = doc.RootElement.GetProperty("values").EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();
        Assert.Equal(Cap, names.Count);
        Assert.Equal(CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Take(Cap), names);

        string warning = SingleTruncationWarning();
        int droppedStatic = seedCount - Cap;
        Assert.Contains(
            ExpectedDropComposition(books.Length + droppedStatic, droppedStatic),
            warning,
            StringComparison.Ordinal);
        Assert.Contains("Tiny Cap Book A", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seedless saturated type (Series carries no seed merge and no generic
    /// word): the reservation is a no-op with no reserved tail, so the JF-825
    /// tail cut stands byte-identical (library head kept, library tail
    /// dropped). Pins that the JF-848 policy changes nothing for types with
    /// nothing to reserve.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SaturatedSeedlessSeries_KeepsTailCutUnchanged()
    {
        const int Cap = 5;
        string[] series = { "Sat Series A", "Sat Series B", "Sat Series C", "Sat Series D", "Sat Series E", "Sat Series F", "Sat Series G", "Sat Series H" };
        _service.MaxCatalogValuesForTest = Cap;
        SetupLibraryWith(BaseItemKind.Series, series);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Sat Series A");
        using var doc = JsonDocument.Parse(payload);
        var names = doc.RootElement.GetProperty("values").EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();
        Assert.Equal(Cap, names.Count);
        Assert.Equal(series.Take(Cap), names);

        Assert.Equal(series.Length, result.SeriesCount);

        string warning = SingleTruncationWarning();
        Assert.Contains(ExpectedDropComposition(series.Length - Cap, 0), warning, StringComparison.Ordinal);
        Assert.Contains("Sat Series F", warning, StringComparison.Ordinal);
        Assert.Contains(Logs(), l => l.Contains("fetch reached the", StringComparison.Ordinal));
    }

    /// <summary>
    /// The JF-874 fifth catalog type: the Movie saturation pin. MovieTitle
    /// carries a 12-value it-IT seed block (JF-874), so a saturated movie
    /// library reserves it whole and cuts the movie head to (cap − 12): the
    /// reservation policy covers all five catalog types, not just the
    /// pre-JF-874 four.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SaturatedMovieLibrary_ReservesMovieSeedSlots()
    {
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Movie).Count;
        string[] movies = Enumerable.Range(1, 15).Select(i => $"Reserved Movie {i:D2}").ToArray();
        int cap = movies.Length;
        _service.MaxCatalogValuesForTest = cap;
        SetupLibraryWith(BaseItemKind.Movie, movies);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Reserved Movie 01");
        using var doc = JsonDocument.Parse(payload);
        var names = doc.RootElement.GetProperty("values").EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();
        Assert.Equal(cap, names.Count);
        Assert.Equal(movies.Take(cap - seedCount), names.Take(cap - seedCount));
        Assert.Equal(CatalogSeedEnrichment.GetSeedNames(CatalogType.Movie), names.Skip(cap - seedCount));

        Assert.Equal(movies.Length, result.MovieCount);

        string warning = SingleTruncationWarning();
        int expectedDropped = movies.Length - (cap - seedCount);
        Assert.Contains(ExpectedDropComposition(expectedDropped, 0), warning, StringComparison.Ordinal);

        // Derived from the live seed count (same rationale as the audiobook pin).
        Assert.Contains($"Reserved Movie {cap - seedCount + 1:D2}", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE JF-823 GENERIC-WORD PIN and the strict-over-cap saturation shape: a
    /// non-it locale leg (es-ES; its word is ASCII, which the raw stored
    /// payload string needs because the JSON encoder escapes non-ASCII, so a
    /// 'Hörbuch' marker could never match) appends the locale's generic word
    /// as the FINAL static entry, and this library sits strictly ABOVE the
    /// cap (not exactly at it), exercising the reserved-tail arithmetic with
    /// libraryDrop &gt; reserved and no static excess. The word, the artifact
    /// the JF-823 live A/B verdict installed and the reservation exists to
    /// protect, must survive as the payload's LAST value. Both locale legs
    /// saturate here, so two truncation warnings fire; the legs are told
    /// apart by their dropped counts (it-IT reserves seedCount slots, es-ES
    /// seedCount + 1 for the word).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SaturatedNonItLocale_ReservesGenericWordLast()
    {
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        string genericWord = CatalogSeedEnrichment.GenericAudiobookWords["es-ES"];
        int reserved = seedCount + 1;
        string[] books = Enumerable.Range(1, 30).Select(i => $"De Book {i:D2}").ToArray();
        const int Cap = 25;
        _service.MaxCatalogValuesForTest = Cap;
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        Plugin.Instance!.Configuration.CatalogSyncLocales = "es-ES";
        try
        {
            var user = TestHelpers.CreateSyncUser();
            var jellyfinUser = TestHelpers.CreateJellyfinUser();

            var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

            Assert.True(result.Success, string.Join("; ", Logs()));

            string payload = ReadStoredPayloadContaining(genericWord);
            using var doc = JsonDocument.Parse(payload);
            var names = doc.RootElement.GetProperty("values").EnumerateArray()
                .Select(v => v.GetProperty("name").GetProperty("value").GetString())
                .ToList();
            Assert.Equal(Cap, names.Count);

            // Library head cut to (cap - seeds - word) ...
            Assert.Equal(books.Take(Cap - reserved), names.Take(Cap - reserved));

            // ... then every seed ...
            Assert.Equal(
                CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook),
                names.Skip(Cap - reserved).Take(seedCount));

            // ... and the generic word SURVIVES as the final value.
            Assert.Equal(genericWord, names[^1]);

            // Both legs truncated; the es-ES leg's warning carries its own
            // dropped count (one more than it-IT's, because es-ES reserves
            // the generic word on top of the seeds), which the full
            // composition phrase selects on its own.
            var warnings = TruncationWarnings();
            Assert.Equal(2, warnings.Count);
            int esDropped = books.Length - (Cap - reserved);
            Assert.Contains(
                warnings,
                w => w.Contains(ExpectedDropComposition(esDropped, 0), StringComparison.Ordinal));
        }
        finally
        {
            Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
        }
    }

    /// <summary>
    /// THE JF-879 FLIPPED BAND PIN: a SMALL library whose payload still
    /// overflows the cap only because the static tail rides along. The
    /// JF-848-era behavior kept every book and cut the seed tail (library
    /// wins over seeds, the JF-541 below-cap reading); the reserve-on-any-cut
    /// policy now makes the library take the loss (rationale:
    /// <see cref="CatalogPayload.TruncateTo"/>). When the reserved tail alone
    /// exceeds the cap (this test's seam shape; production static counts are
    /// tens against a 50000 cap), the library drops whole and the tail's
    /// excess drops after it so the cap still binds.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_OverCapCutReservesSeeds_LibraryTakesTheLoss()
    {
        string[] books = { "Cap Room Book A", "Cap Room Book B", "Cap Room Book C", "Cap Room Book D" };
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        int cap = books.Length + 2;
        _service.MaxCatalogValuesForTest = cap;
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook)[0]);
        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(cap, values.GetArrayLength());

        // The reserved seed floor keeps the FIRST seeds in append order; the
        // seed tail beyond the cap drops after the whole library.
        var names = values.EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();
        Assert.Equal(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Take(cap),
            names);

        int droppedStatic = seedCount - cap;
        string warning = SingleTruncationWarning();
        Assert.Contains(
            ExpectedDropComposition(books.Length + droppedStatic, droppedStatic),
            warning,
            StringComparison.Ordinal);
        Assert.Contains("Cap Room Book A", warning, StringComparison.Ordinal);

        // The library did not saturate the fetch (4 items < 6 cap), so the
        // fetch-saturation signal must stay quiet here.
        Assert.DoesNotContain(Logs(), l => l.Contains("fetch reached the", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE JF-879 JUST-BELOW-CAP CONSEQUENCE PIN, the exact band JF-848
    /// left uncovered: a library one value under the cap (the production
    /// 49999-of-50000 shape, scaled down) whose payload overflows only
    /// through the appended static tail. The tail must survive WHOLE and
    /// the library head lose the same number of values instead (rationale:
    /// <see cref="CatalogPayload.TruncateTo"/>).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_LibraryJustBelowCap_KeepsWholeSeedTail()
    {
        int seedCount = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count;
        const int Cap = 30;
        string[] books = Enumerable.Range(1, Cap - 1).Select(i => $"Band Book {i:D2}").ToArray();
        _service.MaxCatalogValuesForTest = Cap;
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Band Book 01");
        using var doc = JsonDocument.Parse(payload);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(Cap, values.GetArrayLength());

        var names = values.EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString())
            .ToList();

        // The library head keeps (cap - seedCount) books ...
        Assert.Equal(books.Take(Cap - seedCount), names.Take(Cap - seedCount));

        // ... and the ENTIRE static seed tail survives after it.
        Assert.Equal(
            CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook),
            names.Skip(Cap - seedCount));

        int expectedDropped = books.Length - (Cap - seedCount);
        string warning = SingleTruncationWarning();
        Assert.Contains(
            ExpectedDropComposition(expectedDropped, 0),
            warning,
            StringComparison.Ordinal);
        Assert.Contains($"Band Book {Cap - seedCount + 1:D2}", warning, StringComparison.Ordinal);

        // The library sat just under the cap, so the fetch-saturation signal
        // must stay quiet here (the truncation warning is the only signal).
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
        SetupLibraryWith(BaseItemKind.AudioBook, books);
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", Logs()));

        string payload = ReadStoredPayloadContaining("Boundary Book A");
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(cap, doc.RootElement.GetProperty("values").GetArrayLength());

        Assert.Empty(TruncationWarnings());
    }

    /// <summary>
    /// The shared static <see cref="CatalogController"/> payload cache reached
    /// through reflection (it exposes no production clear-all API): the ONE
    /// lookup for both the clear helper below and the read helper after it,
    /// so a CatalogCache reshape breaks one Assert instead of three.
    /// </summary>
    private static System.Collections.IDictionary CatalogCache()
    {
        var cacheField = typeof(CatalogController).GetField(
            "CatalogCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(cacheField != null, "CatalogController.CatalogCache must still exist (this pin's lookup target)");
        return (System.Collections.IDictionary)cacheField!.GetValue(null)!;
    }

    private List<string> Logs() => TestCaptureLogger.Snapshot(_logCapture).Select(l => l.Message).ToList();

    /// <summary>
    /// The drop-composition fragment of LibrarySyncService's truncation
    /// warning (the "dropped {DroppedCount} lowest-priority entries (...)"
    /// tail of its "Truncated {Type} catalog ..." LogWarning template; the
    /// library leg is derived as dropped − static, the same arithmetic the
    /// template itself logs). Every pin asserts against THIS builder, so a
    /// template rewording fails the whole suite loudly instead of drifting
    /// past stale inline literals.
    /// </summary>
    private static string ExpectedDropComposition(int dropped, int staticCount) =>
        $"dropped {dropped} lowest-priority entries ({staticCount} static seed values, {dropped - staticCount} library values)";

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
    /// marker. The per-test PluginTestBase reset empties the cache, so the
    /// marker can only resolve a payload THIS test stored. The second
    /// private copy of the audiobook suite's helper (hoist to TestHelpers on
    /// the third, the GetModelTypeNode convention).
    /// </summary>
    private static string ReadStoredPayloadContaining(string marker)
    {
        foreach (System.Collections.DictionaryEntry entry in CatalogCache())
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
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayBookIntent","slots":[{"name":"book","type":"AudiobookTitle"}]},{"name":"PlayVideoIntent","slots":[{"name":"video","type":"MovieTitle"}]},{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"AudiobookTitle","values":[{"name":{"value":"Il Piccolo Principe"}}]},{"name":"MovieTitle","values":[{"name":{"value":"Il Padrino"}}]},{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
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
                // es-ES listed for the generic-word saturation pin (every
                // synced locale must appear here; extra keys are harmless for
                // the it-IT-only pins, which never iterate them).
                return Json(TestHelpers.SmapiSkillStatusJson("it-IT", "es-ES"));
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
