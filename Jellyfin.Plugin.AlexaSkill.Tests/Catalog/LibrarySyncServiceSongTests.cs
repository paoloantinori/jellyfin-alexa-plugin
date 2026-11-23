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
/// Song-title catalog sync tests (JF-946), the audiobook-suite pattern: the
/// FULL sync runs against a fake SMAPI backend, exercising catalog creation,
/// version upload, ID persistence and the SongTitle model injection through
/// the real CatalogManager. The privacy gate is pinned first (flag off: no
/// song catalog at all), then the preparation pipeline end to end (strip,
/// junk-exclude, play-count ordering, stripped dedup) as it lands in the
/// payload SMAPI would fetch.
/// </summary>
[Collection("Plugin")]
public class LibrarySyncServiceSongTests : PluginTestBase, IDisposable
{
    private const string SongCatalogId = "amzn1.catalog.test.song-1";

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly FakeSmapiHandler _smapiHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibrarySyncService _service;

    public LibrarySyncServiceSongTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _smapiHandler = new FakeSmapiHandler(SongCatalogId);
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
            "alexa-song-sync-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static Entities.User CreateUser(bool songSyncEnabled) => new()
    {
        Id = Guid.NewGuid(),
        InvocationName = "test",
        JellyfinToken = "test-token",
        SmapiDeviceToken = TestHelpers.CreateTestDeviceToken(
            accessToken: "access-token", refreshToken: "refresh-token", expireTimestamp: 9999999999),
        UserSkill = new Entities.UserSkill { SkillId = "amzn1.ask.skill.test-id" },
        VendorId = "test-vendor-id",
        CatalogSongSyncEnabled = songSyncEnabled
    };

    /// <summary>
    /// Only the Audio query returns items (every other type empty), so all
    /// observed SMAPI traffic is song-catalog traffic. Play counts ride the
    /// userdata mock keyed by track name.
    /// </summary>
    private void SetupLibraryWithSongs(IEnumerable<(string Name, int PlayCount, DateTime DateCreated)> tracks)
    {
        var items = tracks.Select(t => new Audio
        {
            Name = t.Name,
            Id = Guid.NewGuid(),
            DateCreated = t.DateCreated
        }).ToList<BaseItem>();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.Audio) == true
                ? items
                : new List<BaseItem>());

        var playCounts = tracks.ToDictionary(t => t.Name, t => t.PlayCount);
        _userDataManagerMock
            .Setup(m => m.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User u, BaseItem item) => new UserItemData
            {
                Key = item.Id.ToString(),
                PlayCount = playCounts.TryGetValue(item.Name ?? string.Empty, out int pc) ? pc : 0
            });
    }

    /// <summary>
    /// THE OFF SWITCH (JF-946; default flipped ON by the maintainer
    /// 2026-11-23): an explicit disabled flag removes the song leg entirely.
    /// A library holding ONLY songs with the flag off must not create any
    /// catalog (the emptiness skip covers the reduced table) and must not
    /// touch the user's catalog ids.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SongsButFlagOff_CreatesNoCatalog()
    {
        SetupLibraryWithSongs(new[] { ("Rapsodia", 3, DateTime.UtcNow) });
        var user = CreateUser(songSyncEnabled: false);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, _smapiHandler.CatalogCreationCount);
        Assert.Null(user.SongCatalogId);
        Assert.Equal(0, result.SongCount);
    }

    /// <summary>
    /// THE PIPELINE (JF-946): with the flag on, the song leg creates the
    /// "Jellyfin Songs" catalog, persists SongCatalogId, uploads a version,
    /// and the payload carries the PREPARED titles: suffixes stripped, junk
    /// excluded, duplicates collapsed on the stripped form with the
    /// most-played representative, play-count ordered, capped nowhere (the
    /// household-scale distinct count sits far under the cap).
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_FlagOn_CreatesCatalog_WithPreparedTitles()
    {
        SetupLibraryWithSongs(new[]
        {
            // Same stripped title twice: the unplayed edition sorts last, the
            // played one is the representative (dedup keeps most played).
            ("Rapsodia (Remaster 2011)", 0, new DateTime(2020, 1, 1)),
            ("Rapsodia", 5, new DateTime(2019, 1, 1)),
            // Junk: bare track number and the "no title" class.
            ("42", 9, new DateTime(2021, 1, 1)),
            ("No Title", 9, new DateTime(2021, 1, 1)),
            // Ordering probes: zero plays but newest, vs many plays but old.
            ("Zebra Song", 0, new DateTime(2024, 6, 1)),
            ("Play Count Anchor", 12, new DateTime(2015, 1, 1))
        });
        var user = CreateUser(songSyncEnabled: true);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var result = await _service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, _smapiHandler.CatalogCreationCount);
        Assert.Contains("Jellyfin Songs", _smapiHandler.CatalogCreationBodies.Single(), StringComparison.Ordinal);
        Assert.Equal(SongCatalogId, user.SongCatalogId);
        Assert.Equal(1, _smapiHandler.VersionUploadsFor(SongCatalogId));

        string payload = ReadStoredPayloadContaining("Play Count Anchor");
        using var doc = JsonDocument.Parse(payload);
        List<string> names = doc.RootElement.GetProperty("values")
            .EnumerateArray()
            .Select(v => v.GetProperty("name").GetProperty("value").GetString()!)
            .ToList();

        // Junk excluded, duplicates collapsed on the stripped form.
        Assert.DoesNotContain(names, n => n == "42" || n.Equals("No Title", StringComparison.OrdinalIgnoreCase));
        Assert.Single(names, n => n == "Rapsodia");
        Assert.DoesNotContain(names, n => n.Contains("(Remaster", StringComparison.Ordinal));

        // Ordering: play count beats recency (the most played first, the
        // alphabetical SortName order the sibling types use would put
        // "Play Count Anchor" after "Rapsodia" and "Zebra Song").
        Assert.Equal("Play Count Anchor", names[0]);

        // The stripped dedup fed the counts: 3 distinct prepared titles
        // (Rapsodia, Zebra Song, Play Count Anchor; the junk pair excluded).
        Assert.Equal(3, result.SongCount);
        Assert.Equal(3, result.SongTitleTotal);

        // The model PUT replaced the static SongTitle seed with the catalog
        // supplier (replace-in-place, the SeriesName shape).
        Assert.NotNull(_smapiHandler.LastModelPutBody);
        var songType = TestHelpers.GetModelTypeNode(_smapiHandler.LastModelPutBody!, "SongTitle");
        var catalog = songType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal(SongCatalogId, catalog.GetProperty("catalogId").GetString());
        Assert.False(songType.TryGetProperty("values", out _), "static seed values must be replaced, not kept alongside the valueSupplier");
    }

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
    /// Fake SMAPI backend, the audiobook fake's shape, with the SongTitle
    /// static seed model the wiring replaces in place.
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

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
