using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        string result = _manager.InjectCatalogReferences(model, "artist-cat-1", null, "3", null);

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

        string result = _manager.InjectCatalogReferences(model, null, "album-cat-2", null, "5");

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        Assert.Equal("AlbumName", types[0].GetProperty("name").GetString());
        Assert.Equal("album-cat-2", types[0].GetProperty("valueSupplier").GetProperty("valueCatalog").GetProperty("catalogId").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_AddsBothCatalogs()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, "artist-1", "album-1", "v1", "v2");

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

        string result = _manager.InjectCatalogReferences(model, "new-artist-cat", null, "2", null);

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

        string result = _manager.InjectCatalogReferences(model, "cat-1", "cat-2", "1", "1");

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

        string result = _manager.InjectCatalogReferences(model, "artist-cat", null, "1", null);

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

        string result = _manager.InjectCatalogReferences(model, "artist-cat", null, "2", null);

        using var resultDoc = JsonDocument.Parse(result);
        var im = resultDoc.RootElement.GetProperty("interactionModel");

        // Interaction-model slot swapped to catalog-backed type.
        string lmSlot = im.GetProperty("languageModel").GetProperty("intents")[0]
            .GetProperty("slots")[0].GetProperty("type").GetString()!;
        Assert.Equal("JellyfinArtist", lmSlot);

        // Dialog-model slot ALSO swapped — this was the MismatchedSlotType bug.
        string dlgSlot = im.GetProperty("dialog").GetProperty("intents")[0]
            .GetProperty("slots")[0].GetProperty("type").GetString()!;
        Assert.Equal("JellyfinArtist", dlgSlot);
    }

    [Fact]
    public void InjectCatalogReferences_DoesNotTouchAlbumIntentSlots()
    {
        string model = BuildInteractionModelJson(
            intents: new[] { ("PlayAlbumIntent", new[] { ("album", "AlbumName") }) });

        string result = _manager.InjectCatalogReferences(model, null, "album-cat", null, "1");

        using var resultDoc = JsonDocument.Parse(result);
        var intents = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("intents");
        // AlbumName has no replacesType, so intent slot stays as AlbumName
        Assert.Equal("AlbumName", intents[0].GetProperty("slots")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void InjectCatalogReferences_NoCatalogs_ReturnsOriginal()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, null, null, null, null);

        Assert.Equal(model, result);
    }

    [Fact]
    public void InjectCatalogReferences_DefaultVersion_WhenNull()
    {
        string model = BuildInteractionModelJson();

        string result = _manager.InjectCatalogReferences(model, "cat-1", null, null, null);

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

        string result = _manager.InjectCatalogReferences(model, "cat-1", "cat-2", "1", "2");

        using var resultDoc = JsonDocument.Parse(result);
        var types = resultDoc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");

        Assert.Equal(2, types.GetArrayLength());
    }

    [Fact]
    public void InjectCatalogReferences_MalformedModel_ReturnsOriginal()
    {
        string model = """{"notAnInteractionModel":true}""";

        string result = _manager.InjectCatalogReferences(model, "cat-1", null, "1", null);

        Assert.Equal(model, result);
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
