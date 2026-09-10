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
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CatalogManagerTests
{
    private readonly CatalogManager _manager;
    private readonly Mock<ILogger<CatalogManager>> _loggerMock;

    public CatalogManagerTests()
    {
        _loggerMock = new Mock<ILogger<CatalogManager>>();
        _manager = new CatalogManager(new StubHttpClientFactory(), _loggerMock.Object);
    }

    #region InjectCatalogReferences

    [Fact]
    public void InjectCatalogReferences_AddsArtistCatalog()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, "artist-cat-1", null, null, "3", null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        var artistType = types[0];
        Assert.Equal("JellyfinArtist", artistType.GetProperty("name").GetString());
        Assert.Equal("CatalogValueSupplier", artistType.GetProperty("valueSupplier").GetProperty("type").GetString());
        Assert.Equal("artist-cat-1", artistType.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal("3", artistType.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("version").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_AddsAlbumCatalog()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, null, "album-cat-2", null, null, "5", null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        Assert.Equal("AlbumName", types[0].GetProperty("name").GetString());
        Assert.Equal("album-cat-2", types[0].GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_AddsSeriesCatalog_ReplacingStaticSeed()
    {
        // JF-493: every locale model declares SeriesName with a static seed list;
        // the series catalog injection must REPLACE that definition in place
        // (same slot type name) instead of adding a parallel type.
        string model = BuildInteractionModelJson(existingTypes: new[]
        {
            ("SeriesName", "{\"type\":\"PLAIN_TEXT\",\"values\":[{\"name\":{\"value\":\"Breaking Bad\"}}]}")
        });

        string result = _manager.InjectCatalogReferences(model, null, null, "series-cat-9", null, null, "4");

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        Assert.Equal("SeriesName", types[0].GetProperty("name").GetString());
        Assert.Equal("CatalogValueSupplier", types[0].GetProperty("valueSupplier").GetProperty("type").GetString());
        Assert.Equal("series-cat-9", types[0].GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal("4", types[0].GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("version").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_SeriesCatalog_DoesNotRetypeSeriesSlots()
    {
        // SeriesName has no ReplacesType: slots already reference it, so the
        // series injection must leave intent slot types untouched.
        string model = BuildInteractionModelJson(
            existingTypes: new[] { ("SeriesName", "{\"type\":\"PLAIN_TEXT\",\"values\":[{\"name\":{\"value\":\"Dark\"}}]}") },
            intents: new[] { ("PlayEpisodeIntent", new[] { ("series_name", "SeriesName") }) });

        string result = _manager.InjectCatalogReferences(model, null, null, "series-cat-9", null, null, "1");

        using var resultDoc = JsonDocument.Parse(result);
        var intents = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("intents");
        Assert.Equal("SeriesName", intents[0].GetProperty("slots")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_AddsBothCatalogs()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, "artist-1", "album-1", null, "v1", "v2", null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(2, types.GetArrayLength());
        Assert.Equal("JellyfinArtist", types[0].GetProperty("name").GetString());
        Assert.Equal("AlbumName", types[1].GetProperty("name").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_ReplacesExistingSlotType()
    {
        string model = BuildInteractionModelJson(existingTypes: new[]
        {
            ("JellyfinArtist", "{\"type\":\"PLAIN_TEXT\",\"values\":[{\"name\":{\"value\":\"OldArtist\",\"synonyms\":[]}}]}")
        });

        string result = _manager.InjectCatalogReferences(model, "new-artist-cat", null, null, "2", null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        Assert.Equal("CatalogValueSupplier", types[0].GetProperty("valueSupplier").GetProperty("type").GetString());
        Assert.Equal("new-artist-cat", types[0].GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_ReplacesExisting_KeepsUnrelated()
    {
        string model = BuildInteractionModelJson(existingTypes: new[]
        {
            ("MediaType", "{\"type\":\"PLAIN_TEXT\",\"values\":[{\"name\":{\"value\":\"Movie\"}}]}"),
            ("JellyfinArtist", "{\"type\":\"PLAIN_TEXT\",\"values\":[]}")
        });

        string result = _manager.InjectCatalogReferences(model, "cat-1", "cat-2", null, "1", "1", null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(3, types.GetArrayLength());
        // MediaType should be preserved unchanged
        Assert.Equal("MediaType", types[0].GetProperty("name").GetString());
        Assert.False(types[0].TryGetProperty("valueSupplier", out _));
    }

    [Fact]
    public void InjectCatalogReferences_UpdatesIntentSlotTypes_Artist()
    {
        string model = BuildInteractionModelJson(
            existingTypes: new[] { ("AMAZON.Musician", "{\"type\":\"PLAIN_TEXT\",\"values\":[]}") },
            intents: new[] { ("PlayMusicIntent", new[] { ("artist", "AMAZON.Musician") }) });

        string result = _manager.InjectCatalogReferences(model, "artist-cat", null, null, "1", null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var intents = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("intents");
        var slots = intents[0].GetProperty("slots");

        Assert.Equal("JellyfinArtist", slots[0].GetProperty("type").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_UpdatesDialogModelSlotTypes_ToMatchInteractionModel()
    {
        // JF-332: SMAPI rejects MismatchedSlotType if dialog.intents[].slots[].type
        // doesn't match the swapped interaction-model slot type. FindSongByArtistIntent.musician
        // stayed AMAZON.Musician after artist catalog injection, failing the model build.
        string model = """
        {
          "interactionModel": {
            "languageModel": {
              "intents": [{"name":"FindSongByArtistIntent","slots":[{"name":"musician","type":"AMAZON.Musician"}]}],
              "types": [{"name":"AMAZON.Musician","type":"PLAIN_TEXT","values":[]}]
            },
            "dialog": {
              "intents": [{"name":"FindSongByArtistIntent","slots":[{"name":"musician","type":"AMAZON.Musician","confirmationRequired":false,"elicitationRequired":false}]}]
            }
          }
        }
        """;

        string result = _manager.InjectCatalogReferences(model, "artist-cat", null, null, "2", null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var im = resultDoc.RootElement.GetProperty("interactionModel");

        // Interaction-model slot swapped to catalog-backed type.
        string lmSlot = im.GetProperty("languageModel").GetProperty("intents")[0]
            .GetProperty("slots")[0].GetProperty("type").GetString()!;
        Assert.Equal("JellyfinArtist", lmSlot);

        // Dialog-model slot ALSO swapped: this was the MismatchedSlotType bug.
        string dlgSlot = im.GetProperty("dialog").GetProperty("intents")[0]
            .GetProperty("slots")[0].GetProperty("type").GetString()!;
        Assert.Equal("JellyfinArtist", dlgSlot);
    }

    [Fact]
    public void InjectCatalogReferences_ArtistCatalog_OnPreSwappedModel_ReplacesSeedInPlace_WithoutRetyping()
    {
        // JF-415: since the en-* + it-IT committed models declare JellyfinArtist
        // (static seed) with musician slots already typed JellyfinArtist in both
        // languageModel and dialog, the artist catalog injection takes the
        // existingIndex >= 0 IN-PLACE-REPLACE branch on every catalog sync of
        // those locales. Before JF-415 no committed model declared JellyfinArtist,
        // so this branch never ran for the artist type; this test pins it.
        string model = """
        {
          "interactionModel": {
            "languageModel": {
              "intents": [
                {"name":"PlaySongIntent","slots":[{"name":"song","type":"AMAZON.MusicRecording"},{"name":"musician","type":"JellyfinArtist"}]},
                {"name":"PlayAlbumIntent","slots":[{"name":"album","type":"AMAZON.MusicRecording"},{"name":"musician","type":"JellyfinArtist"}]},
                {"name":"PlayArtistSongsIntent","slots":[{"name":"musician","type":"JellyfinArtist"}]},
                {"name":"AddToQueueIntent","slots":[{"name":"song","type":"AMAZON.MusicRecording"},{"name":"musician","type":"JellyfinArtist"}]},
                {"name":"PlayNextIntent","slots":[{"name":"song","type":"AMAZON.MusicRecording"},{"name":"musician","type":"JellyfinArtist"}]},
                {"name":"QueryArtistLibraryIntent","slots":[{"name":"musician","type":"JellyfinArtist"},{"name":"query_type","type":"LibraryQueryType"}]},
                {"name":"FindSongByArtistIntent","slots":[{"name":"musician","type":"JellyfinArtist"}]}
              ],
              "types": [
                {"name":"MediaType","values":[{"name":{"value":"song"}}]},
                {"name":"JellyfinArtist","values":[{"name":{"value":"Queen"}},{"name":{"value":"The Beatles"}}]}
              ]
            },
            "dialog": {
              "intents": [
                {"name":"PlaySongIntent","slots":[{"name":"musician","type":"JellyfinArtist","confirmationRequired":false,"elicitationRequired":false}]},
                {"name":"PlayAlbumIntent","slots":[{"name":"musician","type":"JellyfinArtist","confirmationRequired":false,"elicitationRequired":false}]},
                {"name":"FindSongByArtistIntent","slots":[{"name":"musician","type":"JellyfinArtist","confirmationRequired":false,"elicitationRequired":false}]}
              ]
            }
          }
        }
        """;

        string result = _manager.InjectCatalogReferences(model, "artist-cat-7", null, null, "638", null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var im = resultDoc.RootElement.GetProperty("interactionModel");
        var types = im.GetProperty("languageModel").GetProperty("types");

        // (a) exactly ONE JellyfinArtist entry: the seed definition was replaced
        // in place, not duplicated.
        int jellyfinArtistEntries = 0;
        JsonElement artistType = default;
        foreach (JsonElement t in types.EnumerateArray())
        {
            if (string.Equals(t.GetProperty("name").GetString(), "JellyfinArtist", StringComparison.Ordinal))
            {
                jellyfinArtistEntries++;
                artistType = t;
            }
        }

        Assert.Equal(1, jellyfinArtistEntries);

        // (b) the entry carries the catalog supplier and NO static values remain
        // (the seed list must be dropped, not merged).
        Assert.Equal("CatalogValueSupplier", artistType.GetProperty("valueSupplier").GetProperty("type").GetString());
        Assert.Equal("artist-cat-7", artistType.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
        Assert.Equal("638", artistType.GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("version").GetString());
        Assert.False(artistType.TryGetProperty("values", out _));

        // The unrelated type is preserved untouched.
        Assert.Equal("MediaType", types[0].GetProperty("name").GetString());
        Assert.False(types[0].TryGetProperty("valueSupplier", out _));

        // (c) no slot type changed anywhere: every musician slot (all 7
        // languageModel intents + all 3 dialog intents) is still JellyfinArtist
        // (the re-type helpers are no-ops on a pre-swapped model), and the
        // non-musician slots keep their own types.
        foreach (JsonElement intent in im.GetProperty("languageModel").GetProperty("intents").EnumerateArray())
        {
            foreach (JsonElement slot in intent.GetProperty("slots").EnumerateArray())
            {
                string expected = slot.GetProperty("name").GetString() == "musician"
                    ? "JellyfinArtist"
                    : slot.GetProperty("type").GetString()!;
                Assert.Equal(expected, slot.GetProperty("type").GetString());
            }
        }

        foreach (JsonElement dialogIntent in im.GetProperty("dialog").GetProperty("intents").EnumerateArray())
        {
            foreach (JsonElement slot in dialogIntent.GetProperty("slots").EnumerateArray())
            {
                Assert.Equal("JellyfinArtist", slot.GetProperty("type").GetString());
            }
        }

        // (d) no AMAZON.Musician remnants: the built-in is gone from the whole model.
        Assert.DoesNotContain("AMAZON.Musician", result, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectCatalogReferences_DoesNotTouchAlbumIntentSlots()
    {
        string model = BuildInteractionModelJson(
            intents: new[] { ("PlayAlbumIntent", new[] { ("album", "AlbumName") }) });

        string result = _manager.InjectCatalogReferences(model, null, "album-cat", null, null, "1", null);

        using var resultDoc = JsonDocument.Parse(result);
        var intents = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("intents");
        // AlbumName has no replacesType, so intent slot stays as AlbumName
        Assert.Equal("AlbumName", intents[0].GetProperty("slots")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_NoCatalogs_ReturnsOriginal()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, null, null, null, null, null, null);

        Assert.Equal(model, result);
    }

    [Fact]
    public void InjectCatalogReferences_DefaultVersion_WhenNull()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, "cat-1", null, null, null, null, null);

        using var resultDoc = JsonDocument.Parse(result);
        var catalog = resultDoc.RootElement.GetProperty("interactionModel")
            .GetProperty("languageModel").GetProperty("types")[0]
            .GetProperty("valueSupplier").GetProperty("valueCatalog");

        Assert.Equal("1", catalog.GetProperty("version").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_CreatesTypesArray_WhenMissing()
    {
        string model = """{"interactionModel":{"languageModel":{"invocationName":"test","intents":[]}}}""";

        string result = _manager.InjectCatalogReferences(model, "cat-1", "cat-2", null, "1", "2", null);

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(2, types.GetArrayLength());
    }

    [Fact]
    public void InjectCatalogReferences_MalformedModel_ReturnsOriginal()
    {
        string model = """{"notAnInteractionModel":true}""";

        string result = _manager.InjectCatalogReferences(model, "cat-1", null, null, "1", null, null);

        Assert.Equal(model, result);
    }

    [Fact]
    public void InjectCatalogReferences_NullVersion_WarnsOnStaleFallback()
    {
        // JF-495: a null version with a non-empty catalog id pins the stale "1"
        // fallback; the injection must warn so the pin is visible in the logs.
        string model = BuildInteractionModelJson();

        _manager.InjectCatalogReferences(model, "artist-cat-1", null, null, null, null, null);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("stale fallback version", StringComparison.Ordinal)
                    && v.ToString()!.Contains("JellyfinArtist", StringComparison.Ordinal)
                    && v.ToString()!.Contains("artist-cat-1", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void InjectCatalogReferences_SameCatalogIdForTwoTypes_WarnsOnCrossType()
    {
        // JF-495: one catalog id feeding two slot types means one type points at
        // another type's catalog; the injection must warn naming both types.
        string model = BuildInteractionModelJson();

        _manager.InjectCatalogReferences(model, "shared-cat", "shared-cat", null, "3", "3", null);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("another type's catalog", StringComparison.Ordinal)
                    && v.ToString()!.Contains("Artist", StringComparison.Ordinal)
                    && v.ToString()!.Contains("Album", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region UpdateInteractionModelAsync canary (JF-495)

    [Fact]
    public async Task UpdateInteractionModelAsync_BuildSucceeded_CanaryMatches_ReturnsOkResult()
    {
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: null);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);
        Assert.Equal(2, result.PutIntents);
        Assert.Equal(4, result.PutSamples);
        Assert.Equal(2, result.LiveIntents);
        Assert.Equal(4, result.LiveSamples);
        Assert.Null(result.CanaryError);

        // The mandatory pre-PUT audit line (one grep finds every model PUT).
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("MODEL PUT submitting interaction model", StringComparison.Ordinal)
                    && v.ToString()!.Contains("GetModifyPut", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_LiveModelDiffers_CanaryMismatchIsLoud()
    {
        // The JF-495 signature: the build reports SUCCEEDED but the live model
        // carries different counts (a racing deploy replaced it).
        string staleLiveModel = """{"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"OnlyIntent"}]}}}""";
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: staleLiveModel);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.False(result.CanaryMatch);
        Assert.Equal(2, result.PutIntents);
        Assert.Equal(1, result.LiveIntents);
        Assert.Equal(0, result.LiveSamples);
        Assert.NotNull(result.CanaryError);
        Assert.Contains("canary mismatch", result.CanaryError, StringComparison.Ordinal);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("canary MISMATCH", StringComparison.Ordinal)
                    && v.ToString()!.Contains("samples=4", StringComparison.Ordinal)
                    && v.ToString()!.Contains("samples=0", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_NoLocationHeader_TracksBuildViaSkillStatusFallback()
    {
        // Some SMAPI response shapes carry no Location header on the model PUT;
        // the build must still be tracked (via the skill-status endpoint) and the
        // canary must still run.
        var handler = new ModelPutFakeHandler(PutLocation.None, liveModel: null);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);
        Assert.Equal(2, result.LiveIntents);
        Assert.Equal(4, result.LiveSamples);
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_NoCatalogs_ReturnsSkipped()
    {
        var manager = new CatalogManager(new StubHttpClientFactory(), _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal("Skipped", result.BuildStatus);
        Assert.Equal(0, result.PutIntents);
    }

    [Fact]
    public void ExtractLocaleModelStatus_ReadsNestedLocaleStatus()
    {
        string json = """{"manifest":{"lastUpdateRequest":{"status":"SUCCEEDED"}},"interactionModel":{"it-IT":{"lastUpdateRequest":{"status":"IN_PROGRESS"}},"en-US":{"lastUpdateRequest":{"status":"SUCCEEDED"}}}}""";

        Assert.Equal("IN_PROGRESS", CatalogManager.ExtractLocaleModelStatus(json, "it-IT"));
        Assert.Equal("SUCCEEDED", CatalogManager.ExtractLocaleModelStatus(json, "en-US"));
        Assert.Null(CatalogManager.ExtractLocaleModelStatus(json, "de-DE"));
    }

    [Fact]
    public void ExtractLocaleModelStatus_MalformedJson_ReturnsNull()
    {
        Assert.Null(CatalogManager.ExtractLocaleModelStatus("{not json", "it-IT"));
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_SkillStatusLocation_FallsBackImmediately_CanaryFires()
    {
        // JF-497 root cause: the model PUT's Location is a skill-status URL
        // (observed live on the 2026-09-06 startup sync), a shape the
        // update-request poll can never parse; every locale burned its full poll
        // budget, landed TIMEOUT, and the canary never fired. A non-update-request
        // Location must dispatch to the skill-status tracker immediately and
        // resolve within the first poll iterations.
        var handler = new ModelPutFakeHandler(PutLocation.SkillStatus, liveModel: null);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);
        Assert.Equal(2, result.LiveIntents);
        Assert.Equal(4, result.LiveSamples);

        // Settle wait (1 GET) + first fallback poll (1 GET): resolves in the first
        // iterations, not the 30-iteration budget burn of the pre-fix poll.
        Assert.True(handler.StatusGetCount <= 3,
            $"skill-status tracker should resolve within the first poll iterations, got {handler.StatusGetCount} status GETs");
        // The unparseable Location was never polled as an update request.
        Assert.Equal(0, handler.UpdateRequestGetCount);
        // Exactly two model GETs: the settle-time fetch and the canary GET-back
        // (the canary fired once).
        Assert.Equal(2, handler.ModelGetCount);
        Assert.Empty(handler.UnexpectedRequests);

        VerifyLoggedOnce(LogLevel.Information, "non-update-request Location", "JF-497");
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_GenuineUpdateRequestLocation_PollsOperationEndpoint()
    {
        // JF-497: genuine update-request Locations must keep polling through the
        // update-request endpoint (the JF-332/JF-495 behavior), not get silently
        // rerouted to the skill-status tracker.
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: null);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);
        Assert.True(handler.UpdateRequestGetCount >= 1,
            $"the update-request Location must be polled, got {handler.UpdateRequestGetCount} polls");
        Assert.Empty(handler.UnexpectedRequests);
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_SettleWait_UsesNonStagedSkillStatusUrl()
    {
        // JF-497: the settle-wait status GET must hit /v1/skills/{id}/status (the
        // shape Alexa.NET.Management's Skills.Status calls and the live evidence
        // confirms); the stage-scoped /v1/skills/{id}/stages/{stage}/status
        // 404'd for every locale of the 2026-09-06 startup sync.
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: null);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Contains($"GET {ModelPutFakeHandler.SkillStatusUrl}", handler.Requests);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Contains("/stages/", StringComparison.Ordinal)
            && r.EndsWith("/status", StringComparison.Ordinal));
    }

    [Fact]
    public void IsUpdateRequestLocation_UpdateRequestUrls_ReturnsTrue()
    {
        Assert.True(CatalogManager.IsUpdateRequestLocation(
            new Uri("https://api.amazonalexa.com/v1/skills/api/custom/interactionModel/catalogs/cat-1/updateRequest/req-1")));
        Assert.True(CatalogManager.IsUpdateRequestLocation(
            new Uri("https://api.amazonalexa.com/v1/skills/skill-1/stages/development/interactionModel/updateRequest/req-model-1")));
        Assert.True(CatalogManager.IsUpdateRequestLocation(
            new Uri("/v1/skills/api/custom/interactionModel/slotTypes/st-1/updateRequest/req-2", UriKind.Relative)));
    }

    [Fact]
    public void IsUpdateRequestLocation_NonUpdateRequestUrls_ReturnsFalse()
    {
        // The live-observed model PUT Location shape (JF-497) and the stage-scoped
        // variant both lack the updateRequest segment.
        Assert.False(CatalogManager.IsUpdateRequestLocation(
            new Uri("https://api.amazonalexa.com/v1/skills/skill-1/status")));
        Assert.False(CatalogManager.IsUpdateRequestLocation(
            new Uri("https://api.amazonalexa.com/v1/skills/skill-1/stages/development/status")));
        Assert.False(CatalogManager.IsUpdateRequestLocation(
            new Uri("https://api.amazonalexa.com/v1/skills/skill-1/stages/development/interactionModel/locales/it-IT")));
    }

    /// <summary>
    /// Shape of the Location header the fake model PUT returns (JF-497): none
    /// (plain 200), a pollable update-request URL, or the live-observed
    /// skill-status URL that the update-request poll cannot parse.
    /// </summary>
    private enum PutLocation
    {
        None,
        UpdateRequest,
        SkillStatus
    }

    /// <summary>
    /// Fake SMAPI backend for the UpdateInteractionModelAsync flow: skill status
    /// (settle wait; served at the JF-497 non-staged /v1/skills/{id}/status URL),
    /// model GET (first call returns the pre-modification model, later calls
    /// return the "live" model), model PUT (configurable Location shape), and
    /// the update-request poll. The two 404 modes reproduce the JF-502 live
    /// evidence shapes (404 with an HTML body, as Amazon serves for gone resources).
    /// Records every request so tests can assert URL shapes and poll counts.
    /// </summary>
    private sealed class ModelPutFakeHandler : HttpMessageHandler
    {
        private const string Base = "https://api.amazonalexa.com";
        private readonly PutLocation _putLocation;
        private readonly string? _liveModel;
        private readonly bool _pollNotFound;
        private readonly bool _statusNotFound;
        private int _modelGetCount;

        internal static string SkillStatusUrl => $"{Base}/v1/skills/skill-1/status";

        public ModelPutFakeHandler(PutLocation putLocation, string? liveModel, bool pollNotFound = false, bool statusNotFound = false)
        {
            _putLocation = putLocation;
            _liveModel = liveModel;
            _pollNotFound = pollNotFound;
            _statusNotFound = statusNotFound;
        }

        /// <summary>Every request as "METHOD url", in order, for URL-shape assertions.</summary>
        public List<string> Requests { get; } = new();

        /// <summary>Requests that fell through to the fake's unexpected branch.</summary>
        public List<string> UnexpectedRequests { get; } = new();

        public int ModelGetCount => _modelGetCount;

        public int StatusGetCount =>
            Requests.Count(r => r == $"GET {SkillStatusUrl}");

        public int UpdateRequestGetCount =>
            Requests.Count(r => r.StartsWith("GET ", StringComparison.Ordinal)
                && r.Contains("/updateRequest/req-model-1", StringComparison.Ordinal));

        private static string OriginalModel =>
            """{"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[{"name":"PlayEpisodeIntent","slots":[{"name":"series_name","type":"SeriesName"}],"samples":["a","b"]},{"name":"PlayArtistSongsIntent","samples":["c","d"]}],"types":[{"name":"SeriesName","values":[{"name":{"value":"Breaking Bad"}}]}]}}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");

            // JF-497: the settle wait and the fallback tracker must GET the
            // non-staged skill-status URL (the stage-scoped shape 404s live).
            if (request.Method == HttpMethod.Get && url == SkillStatusUrl)
            {
                if (_statusNotFound)
                {
                    return NotFoundHtml();
                }

                return Json("""{"manifest":{"lastUpdateRequest":{"status":"SUCCEEDED"}},"interactionModel":{"it-IT":{"lastUpdateRequest":{"status":"SUCCEEDED"}}}}""");
            }

            if (request.Method == HttpMethod.Put && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                if (_putLocation == PutLocation.None)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                var location = _putLocation == PutLocation.SkillStatus
                    ? new Uri(SkillStatusUrl)
                    : new Uri($"{Base}/v1/skills/skill-1/stages/development/interactionModel/updateRequest/req-model-1");
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = location }
                };
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/req-model-1", StringComparison.Ordinal))
            {
                if (_pollNotFound)
                {
                    return NotFoundHtml();
                }

                return Json("""{"lastUpdateRequest":{"status":"SUCCEEDED"}}""");
            }

            if (request.Method == HttpMethod.Get && url.Contains("/interactionModel/locales/", StringComparison.Ordinal))
            {
                _modelGetCount++;
                return Json(_modelGetCount == 1 || _liveModel == null ? OriginalModel : _liveModel);
            }

            string? body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            UnexpectedRequests.Add($"{request.Method} {url}");
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"error\":\"unexpected {request.Method} {url} {body}\"}}", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        internal static HttpResponseMessage NotFoundHtml() =>
            new(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\"><html><body>404 Not Found</body></html>",
                    Encoding.UTF8,
                    "text/html")
            };
    }

    #endregion

    #region 404 disposition (JF-502)

    [Fact]
    public async Task UpdateInteractionModelAsync_PollReturns404_TreatedAsTerminalCompleted()
    {
        // JF-502: the update-request Location URL is consumed/expired once the
        // build completes, so a poll can get 404 + HTML instead of a status.
        // That must read as terminal-completed (SUCCEEDED disposition, canary
        // still verifies the live model), never as an ERR or a failed locale.
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: null, pollNotFound: true);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);

        VerifyNeverLogged(LogLevel.Error, "SMAPI request failed");
        VerifyLoggedOnce(LogLevel.Debug, "returned 404", "terminal-completed");
    }

    [Fact]
    public async Task UpdateInteractionModelAsync_SkillStatus404_SettledQuietlyWithoutError()
    {
        // JF-502 live evidence: the settle-wait skill-status GET answered 404 for
        // every locale of the 2026-09-06 startup sync, one ERR per locale. The 404
        // must take the quiet no-status path (debug log, settle skipped) instead of
        // ERR + warning, and the sync must proceed with the model update.
        var handler = new ModelPutFakeHandler(PutLocation.UpdateRequest, liveModel: null, statusNotFound: true);
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        var result = await manager.UpdateInteractionModelAsync(
            "token", "skill-1", "development", "it-IT",
            "artist-cat-1", null, null, "3", null, null, CancellationToken.None);

        Assert.Equal("SUCCEEDED", result.BuildStatus);
        Assert.True(result.CanaryMatch);

        VerifyNeverLogged(LogLevel.Error, "SMAPI request failed");
        VerifyNeverLogged(LogLevel.Warning, "Could not read skill status");
        VerifyLoggedOnce(LogLevel.Debug, "returned 404", "no reported status");
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_PollReturns404_TerminalCompletedWithFallbackVersion()
    {
        // JF-502: same terminal-completed disposition on the catalog version poll.
        // The version minted by the upload is unknown, so the existing JF-495
        // "falling back to 1" warning path applies, but no ERR and no throw.
        var handler = new CatalogUploadFakeHandler(() => ModelPutFakeHandler.NotFoundHtml());
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        string version = await manager.UploadCatalogValuesAsync(
            "token", "cat-1", new CatalogPayload(), () => "https://example.test/catalog/x", CancellationToken.None);

        Assert.Equal("1", version);

        VerifyNeverLogged(LogLevel.Error, "SMAPI request failed");
        VerifyLoggedOnce(LogLevel.Debug, "returned 404", "terminal-completed");
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_PollReturns503_StillSurfacesError()
    {
        // Locks the distinction: only the 404 is terminal; any other HTTP failure
        // keeps the transient current behavior (ERR log + HttpRequestException).
        var handler = new CatalogUploadFakeHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var manager = new CatalogManager(
            new StubHttpClientFactory(() => new HttpClient(handler)),
            _loggerMock.Object);

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.UploadCatalogValuesAsync(
            "token", "cat-1", new CatalogPayload(), () => "https://example.test/catalog/x", CancellationToken.None));

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("SMAPI request failed", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Fake SMAPI backend for the UploadCatalogValuesAsync flow: catalog version
    /// creation (202 + Location) and a configurable update-request poll response
    /// (each poll gets a fresh instance; responses are disposed by the caller).
    /// </summary>
    private sealed class CatalogUploadFakeHandler : HttpMessageHandler
    {
        private const string Base = "https://api.amazonalexa.com";
        private readonly Func<HttpResponseMessage> _pollResponse;

        public CatalogUploadFakeHandler(Func<HttpResponseMessage> pollResponse) => _pollResponse = pollResponse;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Post && url.Contains("/catalogs/cat-1/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Headers = { Location = new Uri($"{Base}/v1/skills/api/custom/interactionModel/catalogs/cat-1/updateRequest/req-cat-1") }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/updateRequest/req-cat-1", StringComparison.Ordinal))
            {
                return Task.FromResult(_pollResponse());
            }

            return Task.FromResult(ModelPutFakeHandler.NotFoundHtml());
        }
    }

    private void VerifyNeverLogged(LogLevel level, string messageFragment) =>
        VerifyLogged(level, Times.Never, new[] { messageFragment });

    private void VerifyLoggedOnce(LogLevel level, params string[] messageFragments) =>
        VerifyLogged(level, Times.Once, messageFragments);

    private void VerifyLogged(LogLevel level, Func<Times> times, params string[] messageFragments)
    {
        _loggerMock.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => messageFragments.All(
                    f => v.ToString()!.Contains(f, StringComparison.Ordinal))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }

    #endregion

    #region UpdateIntentSlotTypes

    [Fact]
    public void UpdateIntentSlotTypes_ReplacesMatchingSlotType()
    {
        var languageModel = new JsonObject
        {
            ["intents"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "PlayMusicIntent",
                    ["slots"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "artist", ["type"] = "AMAZON.Musician" },
                        new JsonObject { ["name"] = "song", ["type"] = "AMAZON.Song" }
                    }
                }
            }
        };

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        var slots = languageModel["intents"]![0]!["slots"]!.AsArray();
        Assert.Equal("JellyfinArtist", slots[0]!["type"]!.GetValue<string>());
        Assert.Equal("AMAZON.Song", slots[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void UpdateIntentSlotTypes_MultipleIntents_ReplacesAll()
    {
        var languageModel = new JsonObject
        {
            ["intents"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "PlayMusicIntent",
                    ["slots"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "artist", ["type"] = "AMAZON.Musician" }
                    }
                },
                new JsonObject
                {
                    ["name"] = "SearchArtistIntent",
                    ["slots"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "artist", ["type"] = "AMAZON.Musician" }
                    }
                }
            }
        };

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        foreach (var intent in languageModel["intents"]!.AsArray())
        {
            Assert.Equal("JellyfinArtist", intent!["slots"]![0]!["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public void UpdateIntentSlotTypes_NoMatchingSlots_DoesNothing()
    {
        var languageModel = new JsonObject
        {
            ["intents"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "PlayIntent",
                    ["slots"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "media", ["type"] = "AMAZON.MusicRecording" }
                    }
                }
            }
        };

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        Assert.Equal("AMAZON.MusicRecording", languageModel["intents"]![0]!["slots"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void UpdateIntentSlotTypes_NoIntents_DoesNothing()
    {
        var languageModel = new JsonObject { ["intents"] = new JsonArray() };

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        Assert.Empty(languageModel["intents"]!.AsArray());
    }

    [Fact]
    public void UpdateIntentSlotTypes_NullIntents_DoesNothing()
    {
        var languageModel = new JsonObject();

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        Assert.Null(languageModel["intents"]);
    }

    [Fact]
    public void UpdateIntentSlotTypes_IntentWithNoSlots_SkipsGracefully()
    {
        var languageModel = new JsonObject
        {
            ["intents"] = new JsonArray
            {
                new JsonObject { ["name"] = "AMAZON.StopIntent" }
            }
        };

        _manager.UpdateIntentSlotTypes(languageModel, "AMAZON.Musician", "JellyfinArtist");

        Assert.Null(languageModel["intents"]![0]!["slots"]);
    }

    #endregion

    #region ResolveLocationUri

    [Fact]
    public void ResolveLocationUri_AbsoluteUri_ReturnedAsIs()
    {
        var uri = new Uri("https://api.amazonalexa.com/v1/skills/something");

        var result = CatalogManagerTestsAccessor.InvokeResolveLocationUri(uri);

        Assert.Equal(uri, result);
    }

    [Fact]
    public void ResolveLocationUri_RelativeUri_ResolvedAgainstBase()
    {
        var uri = new Uri("/v1/skills/api/custom/interactionModel/catalogs/cat-1/updateRequest/abc", UriKind.Relative);

        var result = CatalogManagerTestsAccessor.InvokeResolveLocationUri(uri);

        Assert.True(result.IsAbsoluteUri);
        Assert.Equal("https://api.amazonalexa.com/v1/skills/api/custom/interactionModel/catalogs/cat-1/updateRequest/abc", result.ToString());
    }

    #endregion

    #region CatalogController Cache

    [Fact]
    public void CatalogController_StoreAndRetrieve()
    {
        string payload = """{"values":[{"id":"v1","name":{"value":"Queen"}}]}""";

        string key = Jellyfin.Plugin.AlexaSkill.Controller.CatalogController.StorePayload(payload);

        Assert.False(string.IsNullOrEmpty(key));
        Assert.NotEqual(payload, key);
    }

    [Fact]
    public void CatalogController_KeysAreUnique()
    {
        string payload = """{"values":[]}""";

        string key1 = Jellyfin.Plugin.AlexaSkill.Controller.CatalogController.StorePayload(payload);
        string key2 = Jellyfin.Plugin.AlexaSkill.Controller.CatalogController.StorePayload(payload);

        Assert.NotEqual(key1, key2);
    }

    #endregion

    #region Helpers

    private static string BuildInteractionModelJson(
        (string Name, string ValueSupplier)[]? existingTypes = null,
        (string Name, (string SlotName, string SlotType)[] Slots)[]? intents = null)
    {
        var root = new JsonObject
        {
            ["interactionModel"] = new JsonObject
            {
                ["languageModel"] = new JsonObject
                {
                    ["invocationName"] = "test skill",
                    ["types"] = new JsonArray(),
                    ["intents"] = new JsonArray()
                }
            }
        };

        var typesArray = root["interactionModel"]!["languageModel"]!["types"]!.AsArray();
        if (existingTypes != null)
        {
            foreach (var t in existingTypes)
            {
                var typeObj = JsonNode.Parse($"{{\"name\":\"{t.Name}\",\"values\":[{{\"name\":{{\"value\":\"test\"}}}}]}}");
                typesArray.Add(typeObj);
            }
        }

        var intentsArray = root["interactionModel"]!["languageModel"]!["intents"]!.AsArray();
        if (intents != null)
        {
            foreach (var intent in intents)
            {
                var intentObj = new JsonObject { ["name"] = intent.Name };
                var slotsArray = new JsonArray();
                foreach (var (slotName, slotType) in intent.Slots)
                {
                    slotsArray.Add(new JsonObject { ["name"] = slotName, ["type"] = slotType });
                }

                intentObj["slots"] = slotsArray;
                intentsArray.Add(intentObj);
            }
        }

        return root.ToJsonString();
    }

    #endregion
}

/// <summary>
/// Minimal IHttpClientFactory stub. The default constructor returns a bare client
/// (for tests that don't make HTTP calls); the Func overload injects a handler-bound
/// client (for SMAPI-flow tests that need per-request dispatch).
/// </summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly Func<HttpClient> _create;
    public StubHttpClientFactory() : this(() => new HttpClient()) { }
    public StubHttpClientFactory(Func<HttpClient> create) => _create = create;
    public HttpClient CreateClient(string name) => _create();
}

/// <summary>
/// Provides access to private static methods on CatalogManager for testing.
/// </summary>
internal static class CatalogManagerTestsAccessor
{
    public static Uri InvokeResolveLocationUri(Uri uri)
    {
        // ResolveLocationUri is internal static, so we can call it directly
        // via the test assembly's InternalsVisibleTo access
        return InvokeResolveLocationUriCore(uri);
    }

    private static Uri InvokeResolveLocationUriCore(Uri uri)
    {
        // Use reflection since ResolveLocationUri is private static
        var method = typeof(CatalogManager).GetMethod(
            "ResolveLocationUri",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (method == null)
        {
            throw new InvalidOperationException("Could not find ResolveLocationUri method");
        }

        return (Uri)method.Invoke(null, new object[] { uri })!;
    }
}
