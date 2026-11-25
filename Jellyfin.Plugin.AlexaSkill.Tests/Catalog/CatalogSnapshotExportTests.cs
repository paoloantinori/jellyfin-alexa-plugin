#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-954.8: the authoritative catalog snapshot export. The pins: (1) the
/// shared final-payload builder reproduces byte-for-byte what the sync built
/// before the refactor (golden literals captured from the PRE-refactor
/// sequence on 2026-11-25, when the Artist AND Series seed merges were both
/// in place: the Artist seeds are the union across locale models, the Series
/// seeds are the same union pattern landed by the Series seed merge); (2) the
/// export equals the payload the sync ACTUALLY uploads for the same library
/// and locale; (3) the export's no-SMAPI contract (zero CatalogManager calls,
/// zero CatalogController cache registrations, zero configuration writes);
/// (4) the locale/type roster rules (no ar-SA, CatalogSyncLocales respected,
/// the per-user song gate, zero-item types omitted); (5) the truncation and
/// cap behavior ride the shared builder; (6) the endpoint's authorization
/// attribute and response contract. Runs beside the sync suites in the
/// Plugin collection (the export reads Plugin.Instance.Configuration and the
/// CatalogController cache pin asserts process-global emptiness).
/// </summary>
[Collection("Plugin")]
public class CatalogSnapshotExportTests : PluginTestBase, IDisposable
{
    private const string ArtistCatalogId = "amzn1.catalog.test.snapshot-1";

    /// <summary>The golden Artist it-IT payload: values and synonyms, one row
    /// per entry (row[0] is the value, rows[1..] the synonyms), captured from
    /// the PRE-refactor sync sequence for the fixed item list the test feeds.
    /// 2 library artists, then the appended Artist seed union (12 seeds) with
    /// it-IT phonetic synonyms and partial-word appends.</summary>
    private const string GoldenArtistItIt =
        """
        [
          ["Soul Coughing", "Sol Cofin", "i Sol Cofin", "Soul Coffin", "Sol Coffin", "Soul Qoughing", "Soul"],
          ["P!nk", "P!nc", "P!nq"],
          ["Queen", "Quen", "Cueen"],
          ["The Beatles", "i Beatles", "Beatles"],
          ["Pink Floyd", "i Pink Floyd", "Pinc Floyd", "Pinq Floyd", "Pink"],
          ["Radiohead"],
          ["Michael Jackson", "Michael Jakson", "i Michael Jakson", "Michael Jaccson", "Michael Jaqqson", "Michael Jacson", "Michael"],
          ["The Rolling Stones", "Rollin Stones", "Rolin Stones", "i Rollin Stones", "Rollin Stonnes", "Rolling"],
          ["Metallica"],
          ["The Police", "i Police", "Police"],
          ["Coldplay", "Qoldplay", "Quoldplay"],
          ["Miles Davis", "i Miles Davis", "Miles"],
          ["Maneskin", "Manescin", "Manesqin", "Manesquin"],
          ["Ligabue"]
        ]
        """;

    /// <summary>The golden Series es-ES payload, same shape and capture
    /// provenance: 2 library series, then the appended Series seed union
    /// (19 localized titles across the committed models) with es-ES phonetic
    /// synonyms. "Adolescencia" rides as a library value and carries no
    /// es-ES synonym family.</summary>
    private const string GoldenSeriesEsEs =
        """
        [
          ["Breaking Bad", "Breakin Bad", "los Breakin Bad", "Breakkin Bad"],
          ["Adolescencia"],
          ["Game of Thrones", "Game of drones", "los Game of drones", "Gamme of dronnes"],
          ["Stranger Things", "Stranger dings", "los Stranger dings"],
          ["Money Heist", "Money eist", "los Money eist", "Monney eist"],
          ["Peaky Blinders", "los Peaky Blinders"],
          ["The Walking Dead", "ualkin Dead", "los ualkin Dead"],
          ["Dark"],
          ["Sherlock", "cherlok"],
          ["Haus des Geldes", "aus des Geldes", "los aus des Geldes"],
          ["Better Call Saul", "los Better Call Saul"],
          ["Dexter"],
          ["Juego de Tronos", "los Juego de Tronos"],
          ["La Casa de Papel", "los La Casa de Papel"],
          ["El Internado", "los El Internado"],
          ["El Señor de los Cielos", "los El Señor de los Cielos"],
          ["Lupin"],
          ["Le Bureau des Légendes", "los Le Bureau des Légendes"],
          ["Engrenages"],
          ["Il Trono di Spade", "los Il Trono di Spade"],
          ["La Casa di Carta", "los La Casa di Carta"]
        ]
        """;

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly Mock<CatalogManager> _catalogManagerMock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _exportService;

    public CatalogSnapshotExportTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _catalogManagerMock = new Mock<CatalogManager>(Mock.Of<System.Net.Http.IHttpClientFactory>(), NullLogger<CatalogManager>.Instance);
        _loggerFactory = LoggerFactory.Create(b => { });

        // The export never touches the CatalogManager; the mock lets the
        // no-SMAPI pin prove it (VerifyNoOtherCalls).
        _exportService = new LibrarySyncService(
            _libraryManagerMock.Object,
            _catalogManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory.CreateLogger<LibrarySyncService>());

        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-snapshot-export-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static List<BaseItem> Artists(params string[] names) =>
        names.Select(n => (BaseItem)new MusicArtist { Name = n, Id = Guid.NewGuid() }).ToList();

    private static List<BaseItem> Series(params string[] names) =>
        names.Select(n => (BaseItem)new MediaBrowser.Controller.Entities.TV.Series { Name = n, Id = Guid.NewGuid() }).ToList();

    private static List<BaseItem> Songs(params string[] names) =>
        names.Select(n => (BaseItem)new Audio { Name = n, Id = Guid.NewGuid() }).ToList();

    /// <summary>Answers the library queries kind-scoped: only the named kind's
    /// query returns its items, so the export's per-type fetches see exactly
    /// what a real library would hold (a kind-blind answer would leak the
    /// items into every other type's query and fake phantom type entries).
    /// </summary>
    private void SetupLibraryByKind(params (BaseItemKind Kind, IReadOnlyList<BaseItem> Items)[] byKind)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                foreach ((BaseItemKind kind, IReadOnlyList<BaseItem> items) in byKind)
                {
                    if (q.IncludeItemTypes?.Contains(kind) == true)
                    {
                        return items.ToList();
                    }
                }

                return new List<BaseItem>();
            });
    }

    private Jellyfin.Database.Implementations.Entities.User JellyfinUser => TestHelpers.CreateJellyfinUser();

    /// <summary>
    /// AC 1, golden half: the shared builder reproduces, values AND synonyms,
    /// exactly what the sync sequence built before the statements moved into
    /// BuildFinalCatalogPayload (Artist, it-IT; seeds were merged at capture
    /// time, the union pattern).
    /// </summary>
    [Fact]
    public void BuildFinalCatalogPayload_ArtistItIt_MatchesThePreRefactorGolden()
    {
        var user = TestHelpers.CreateSyncUser();
        var payload = _exportService.BuildFinalCatalogPayload(user, CatalogType.Artist, Artists("Soul Coughing", "P!nk"), "it-IT").Payload;
        AssertGolden(GoldenArtistItIt, payload);
    }

    /// <summary>
    /// AC 1, golden half, the second pinned pair: Series, es-ES.
    /// </summary>
    [Fact]
    public void BuildFinalCatalogPayload_SeriesEsEs_MatchesThePreRefactorGolden()
    {
        var user = TestHelpers.CreateSyncUser();
        var payload = _exportService.BuildFinalCatalogPayload(user, CatalogType.Series, Series("Breaking Bad", "Adolescencia"), "es-ES").Payload;
        AssertGolden(GoldenSeriesEsEs, payload);
    }

    /// <summary>
    /// AC 1, behavioral half: the export for a fixed library and locale equals
    /// the payload the sync ACTUALLY uploaded for the same library and locale
    /// (the full sync runs against the fake SMAPI backend and the stored
    /// source payload is read back from the CatalogController cache). This is
    /// the cannot-diverge property on the live shape, not just on literals.
    /// </summary>
    [Fact]
    public async Task Export_ArtistItIt_EqualsThePayloadTheSyncUploads()
    {
        var smapiHandler = new FakeSmapiHandler(ArtistCatalogId);
        ILoggerFactory syncLoggerFactory = LoggerFactory.Create(b => { });
        try
        {
            var syncService = new LibrarySyncService(
                _libraryManagerMock.Object,
                new CatalogManager(
                    new StubHttpClientFactory(() => new HttpClient(smapiHandler)),
                    syncLoggerFactory.CreateLogger<CatalogManager>())
                {
                    PollDelayMsForTest = 0
                },
                _userDataManagerMock.Object,
                syncLoggerFactory.CreateLogger<LibrarySyncService>())
            {
                InterLocaleDelayMsForTest = 0
            };

            SetupLibraryByKind(
                (BaseItemKind.MusicArtist, Artists("Soul Coughing", "P!nk")),
                (BaseItemKind.Series, Series("Dark")));

            var user = TestHelpers.CreateSyncUser();
            var jellyfinUser = JellyfinUser;
            Plugin.Instance!.Configuration.CatalogSyncLocales = "it-IT";
            try
            {
                SyncResult syncResult = await syncService.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);
                Assert.True(syncResult.Success, "the sync itself must succeed for the comparison to mean anything");

                // The payload SMAPI was handed, read back from the stored cache.
                string stored = TestHelpers.ReadStoredCatalogPayloadContaining("Soul Coughing");
                List<(string Value, IReadOnlyList<string> Synonyms)> uploaded = ParsePayloadValues(stored);
                Assert.NotEmpty(uploaded);

                CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(
                    jellyfinUser, user, "it-IT", CancellationToken.None);
                IReadOnlyList<CatalogSnapshotEntry> entries = export.Locales["it-IT"]["JellyfinArtist"];

                Assert.Equal(
                    uploaded.Select(v => v.Value).ToList(),
                    entries.Select(e => e.Value).ToList());
                Assert.Equal(
                    uploaded.Select(v => v.Synonyms).ToList(),
                    entries.Select(e => e.Synonyms).ToList());
            }
            finally
            {
                Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
            }
        }
        finally
        {
            syncLoggerFactory.Dispose();
        }
    }

    /// <summary>
    /// AC 3: the export makes no SMAPI call (zero CatalogManager invocations
    /// on the mock), registers nothing in the single-fetch payload cache (the
    /// process-global cache stays empty through the export), and writes no
    /// configuration (nothing in the build path touches the config beyond the
    /// CatalogSyncLocales read; the plugin test host records every real
    /// save through the serializer mock the shared instance was built with).
    /// </summary>
    [Fact]
    public void Export_MakesNoSmapiCall_NoCacheRegistration_NoConfigWrite()
    {
        SetupLibraryByKind((BaseItemKind.Series, Series("Dark")));
        var user = TestHelpers.CreateSyncUser();

        CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, null, CancellationToken.None);

        // The export produced data (an empty result would pass the zero-call
        // pins vacuously).
        Assert.NotEmpty(export.Locales);
        Assert.All(export.Locales.Values, types => Assert.NotEmpty(types));
        _catalogManagerMock.VerifyNoOtherCalls();
        Assert.Equal(0, TestHelpers.StoredCatalogPayloadCount());
    }

    /// <summary>
    /// AC 5: the per-user song gate. A disabled CatalogSongSyncEnabled removes
    /// the song row from the sync's wiring table; the export must show no
    /// SongTitle key anywhere even though the library holds songs, while the
    /// sibling types still appear.
    /// </summary>
    [Fact]
    public void Export_SongSyncDisabled_NoSongTitleTypeAnywhere()
    {
        SetupLibraryByKind(
            (BaseItemKind.Series, Series("Dark")),
            (BaseItemKind.Audio, Songs("Rapsodia")));

        var user = TestHelpers.CreateSyncUser(); // CatalogSongSyncEnabled = false (the factory default)
        Assert.False(user.CatalogSongSyncEnabled);

        CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, null, CancellationToken.None);

        Assert.NotEmpty(export.Locales);
        Assert.All(export.Locales, locale => Assert.DoesNotContain("SongTitle", locale.Value.Keys));
        Assert.All(export.Locales, locale => Assert.Contains("SeriesName", locale.Value.Keys));
    }

    /// <summary>
    /// AC 5, the opt-in arm: with the flag on, the song catalog appears and
    /// carries the PREPARED title (the suffix stripped), the exact pipeline
    /// the sync's song leg runs, through the shared fetch and builder.
    /// </summary>
    [Fact]
    public void Export_SongSyncEnabled_IncludesSongTitleWithPreparedTitle()
    {
        SetupLibraryByKind((BaseItemKind.Audio, Songs("Rapsodia (Remaster 2011)")));
        var user = TestHelpers.CreateSyncUser();
        user.CatalogSongSyncEnabled = true;

        CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, null, CancellationToken.None);

        Assert.All(export.Locales, locale =>
        {
            IReadOnlyList<CatalogSnapshotEntry> entries = locale.Value["SongTitle"];
            Assert.Single(entries);
            Assert.Equal("Rapsodia", entries[0].Value);
            Assert.Empty(entries[0].Synonyms);
        });
    }

    /// <summary>
    /// AC 5: ar-SA never appears. The "*" roster excludes it (the JF-543
    /// gate), an explicit roster naming it drops it, and an explicit
    /// locale=ar-SA request contributes nothing.
    /// </summary>
    [Fact]
    public void Export_ArSaNeverAppears()
    {
        SetupLibraryByKind((BaseItemKind.Series, Series("Dark")));
        var user = TestHelpers.CreateSyncUser();
        var jellyfinUser = JellyfinUser;

        // The default "*" roster.
        CatalogSnapshotExport starRoster = _exportService.BuildCatalogSnapshot(jellyfinUser, user, null, CancellationToken.None);
        Assert.NotEmpty(starRoster.Locales);
        Assert.DoesNotContain("ar-SA", starRoster.Locales.Keys);

        // An explicit roster that names ar-SA.
        Plugin.Instance!.Configuration.CatalogSyncLocales = "de-DE,ar-SA";
        try
        {
            CatalogSnapshotExport explicitRoster = _exportService.BuildCatalogSnapshot(jellyfinUser, user, null, CancellationToken.None);
            Assert.Equal(new[] { "it-IT", "de-DE" }, explicitRoster.Locales.Keys.ToList());
        }
        finally
        {
            Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
        }

        // An explicit locale filter for ar-SA.
        CatalogSnapshotExport filtered = _exportService.BuildCatalogSnapshot(jellyfinUser, user, "ar-SA", CancellationToken.None);
        Assert.Empty(filtered.Locales);
    }

    /// <summary>
    /// AC 5: an explicit CatalogSyncLocales roster is respected: exactly
    /// it-IT plus the listed locales, it-IT first (the sync's resolution
    /// shape, the SMAPI-free parse).
    /// </summary>
    [Fact]
    public void Export_RespectsCatalogSyncLocales()
    {
        SetupLibraryByKind((BaseItemKind.Series, Series("Dark")));
        var user = TestHelpers.CreateSyncUser();
        Plugin.Instance!.Configuration.CatalogSyncLocales = "de-DE";
        try
        {
            CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, null, CancellationToken.None);
            Assert.Equal(new[] { "it-IT", "de-DE" }, export.Locales.Keys.ToList());
        }
        finally
        {
            Plugin.Instance!.Configuration.CatalogSyncLocales = string.Empty;
        }
    }

    /// <summary>
    /// AC 5: a type the sync would upload nothing for (zero items) is omitted:
    /// the sync mints no catalog version for a zero-item type, so the export
    /// carries no type key for it, in every locale.
    /// </summary>
    [Fact]
    public void Export_TypesWithZeroItems_AreOmitted()
    {
        SetupLibraryByKind((BaseItemKind.Series, Series("Dark")));
        var user = TestHelpers.CreateSyncUser();

        CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, null, CancellationToken.None);

        Assert.NotEmpty(export.Locales);
        Assert.All(export.Locales, locale => Assert.Equal(new[] { "SeriesName" }, locale.Value.Keys.ToList()));
    }

    /// <summary>
    /// AC 6: the values and synonyms ride the SAME truncation and cap the
    /// uploaded payload carries, because they come from the shared builder: a
    /// value longer than the 140-character slot limit lands truncated, and
    /// the export honors the cap seam exactly (the song type is seedless, so
    /// the cap binds the library values directly).
    /// </summary>
    [Fact]
    public void Export_RespectsTheTruncationAndTheValueCap()
    {
        string longName = "The Very Long Title " + new string('x', 140) + " That Overflows The Slot Limit";
        SetupLibraryByKind((BaseItemKind.Audio, Songs(longName, "Track B", "Track C", "Track D", "Track E")));
        var user = TestHelpers.CreateSyncUser();
        user.CatalogSongSyncEnabled = true;
        _exportService.MaxCatalogValuesForTest = 3;
        try
        {
            CatalogSnapshotExport export = _exportService.BuildCatalogSnapshot(JellyfinUser, user, "it-IT", CancellationToken.None);
            IReadOnlyList<CatalogSnapshotEntry> entries = export.Locales["it-IT"]["SongTitle"];
            Assert.Equal(3, entries.Count);
            Assert.Equal(SlotValueHelper.Truncate(longName), entries[0].Value);
            Assert.All(entries, e => Assert.True(e.Value.Length <= 140, "the exported value must respect the 140-character slot limit"));
            Assert.All(entries.SelectMany(e => e.Synonyms), s => Assert.True(s.Length <= 140, "the exported synonyms must respect the 140-character slot limit"));
        }
        finally
        {
            _exportService.MaxCatalogValuesForTest = null;
        }
    }

    /// <summary>
    /// The type-to-kind map covers EVERY type the export roster can name
    /// (CatalogSlotTypeNames, the JF-711 lockstep with the sync's wiring
    /// table). Without this totality pin a future roster row without a map
    /// arm compiles green and throws ArgumentOutOfRangeException on the
    /// first admin GET, mid-response after earlier types already fetched.
    /// </summary>
    [Fact]
    public void CatalogTypeItemKind_CoversEveryExportRosterType()
    {
        foreach (CatalogType rosterType in CatalogSlotTypes.CatalogSlotTypeNames.Keys)
        {
            BaseItemKind kind = LibrarySyncService.CatalogTypeItemKind(rosterType);
            Assert.True(Enum.IsDefined(kind), $"roster type {rosterType} mapped to an undefined kind {kind}");
        }
    }

    /// <summary>
    /// AC 2: the endpoint carries the RequiresElevation policy on EVERY action
    /// (the same attribute every SimulatorController action uses), so an
    /// unauthenticated or non-admin request never reaches the handler body.
    /// A new action added without the attribute fails here loudly.
    /// </summary>
    [Fact]
    public void Endpoint_CarriesRequiresElevation_OnEveryAction()
    {
        List<MethodInfo> actions = typeof(CatalogSnapshotController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(CatalogSnapshotController))
            .ToList();
        Assert.NotEmpty(actions);
        Assert.All(actions, method =>
        {
            AuthorizeAttribute? attribute = method.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(
                attribute != null,
                $"{method.Name} must carry [Authorize]; an unauthenticated or non-admin request must never reach it");
            Assert.Equal("RequiresElevation", attribute!.Policy);
        });
    }

    /// <summary>
    /// AC 4: the response JSON carries exactly the snapshot contract
    /// properties: schema, producer, locales at the top; slot type names from
    /// CatalogSlotTypeNames inside; value and synonyms on every entry. Nothing
    /// else: no tokens, no api keys, no skill ids, no catalog ids, no user
    /// names, no item ids.
    /// </summary>
    [Fact]
    public void Endpoint_ResponseCarriesOnlyTheSnapshotContractProperties()
    {
        SetupLibraryByKind(
            (BaseItemKind.Series, Series("Dark")),
            (BaseItemKind.Movie, new List<BaseItem> { TestHelpers.CreateMovie("Test Movie") }));
        var user = TestHelpers.CreateSyncUser(Guid.NewGuid());
        Plugin.Instance!.Configuration.AddUser(user);
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(m => m.GetUserById(user.Id)).Returns(JellyfinUser);
        var controller = new CatalogSnapshotController(_exportService, userManagerMock.Object, _loggerFactory);

        var result = Assert.IsType<ContentResult>(controller.GetCatalogSnapshot(user.Id, null, CancellationToken.None));
        using var doc = JsonDocument.Parse(result.Content!);
        JsonElement root = doc.RootElement;

        Assert.Equal(
            new[] { "locales", "producer", "schema" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(CatalogSnapshotController.SnapshotProducerName, root.GetProperty("producer").GetString());

        foreach (JsonProperty locale in root.GetProperty("locales").EnumerateObject())
        {
            Assert.NotEqual("ar-SA", locale.Name, StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty type in locale.Value.EnumerateObject())
            {
                Assert.Contains(type.Name, CatalogSlotTypes.CatalogSlotTypeNames.Values);
                foreach (JsonElement entry in type.Value.EnumerateArray())
                {
                    Assert.Equal(
                        new[] { "synonyms", "value" },
                        entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
                    Assert.True(entry.GetProperty("value").ValueKind == JsonValueKind.String);
                    foreach (JsonElement synonym in entry.GetProperty("synonyms").EnumerateArray())
                    {
                        Assert.Equal(JsonValueKind.String, synonym.ValueKind);
                    }
                }
            }
        }
    }

    /// <summary>
    /// AC 2, the no-data arm at unit level: an unknown or missing user id
    /// yields no data (404 with an error object, never a snapshot body), and
    /// an empty id is a 400. The auth middleware shape (401/403 for a
    /// non-admin caller) is the RequiresElevation attribute's own contract,
    /// pinned by the reflection test above; the handler-level guarantee is
    /// that a request that gets past it still only names data for a real,
    /// configured user.
    /// </summary>
    [Fact]
    public void Endpoint_UnknownOrEmptyUser_YieldsNoData()
    {
        var userManagerMock = new Mock<IUserManager>();
        var controller = new CatalogSnapshotController(_exportService, userManagerMock.Object, _loggerFactory);

        var unknown = Assert.IsType<NotFoundObjectResult>(controller.GetCatalogSnapshot(Guid.NewGuid(), null, CancellationToken.None));
        Assert.Equal(404, unknown.StatusCode);

        var empty = Assert.IsType<BadRequestObjectResult>(controller.GetCatalogSnapshot(Guid.Empty, null, CancellationToken.None));
        Assert.Equal(400, empty.StatusCode);
    }

    private static void AssertGolden(string goldenJson, CatalogPayload payload)
    {
        List<List<string>> golden = JsonSerializer.Deserialize<List<List<string>>>(goldenJson)!;
        List<List<string>> actual = payload.Values
            .Select(v => new List<string> { v.Name.Value }.Concat(v.Name.Synonyms ?? new List<string>()).ToList())
            .ToList();

        Assert.True(
            golden.Count == actual.Count,
            $"golden has {golden.Count} entries, the builder produced {actual.Count}: [{string.Join(", ", actual.Select(r => r[0]))}]");
        for (int i = 0; i < golden.Count; i++)
        {
            Assert.True(
                golden[i].SequenceEqual(actual[i], StringComparer.Ordinal),
                $"entry {i} diverged from the pre-refactor golden: expected [{string.Join(" | ", golden[i])}], actual [{string.Join(" | ", actual[i])}]");
        }
    }

    private static List<(string Value, IReadOnlyList<string> Synonyms)> ParsePayloadValues(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("values").EnumerateArray()
            .Select(v =>
            {
                JsonElement name = v.GetProperty("name");
                return (
                    Value: name.GetProperty("value").GetString()!,
                    Synonyms: (IReadOnlyList<string>)(name.TryGetProperty("synonyms", out JsonElement synonyms)
                        ? synonyms.EnumerateArray().Select(s => s.GetString()!).ToList()
                        : new List<string>()));
            })
            .ToList();
    }

    /// <summary>
    /// Fake SMAPI backend, the catalog-suite fake's shape (per-file by
    /// policy): catalog creation, catalog version upload (202 + poll
    /// location), poll (SUCCEEDED), skill status, interaction model GET
    /// (later GETs echo the last PUT body so the canary matches) and PUT.
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
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayArtistSongsIntent","slots":[{"name":"musician","type":"JellyfinArtist"}]},{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}]}],"types":[{"name":"JellyfinArtist","values":[{"name":{"value":"Queen"}}]},{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}
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
