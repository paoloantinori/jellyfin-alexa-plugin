using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CatalogModelsTests
{
    [Fact]
    public void FormatId_ArtistType_ProducesCorrectFormat()
    {
        var guid = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        string id = CatalogValue.FormatId(CatalogType.Artist, guid);
        Assert.Equal("jellyfin_artist_a1b2c3d4e5f67890abcdef1234567890", id);
    }

    [Fact]
    public void FormatId_AlbumType_ProducesCorrectFormat()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string id = CatalogValue.FormatId(CatalogType.Album, guid);
        Assert.StartsWith("jellyfin_album_", id);
        Assert.DoesNotContain('-', id);
    }

    [Fact]
    public void FormatId_SongType_ProducesCorrectFormat()
    {
        var guid = Guid.NewGuid();
        string id = CatalogValue.FormatId(CatalogType.Song, guid);
        Assert.StartsWith("jellyfin_song_", id);
    }

    [Fact]
    public void FromItems_CreatesPayloadWithCorrectValues()
    {
        var items = new[]
        {
            (Id: Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"), Name: "Queen"),
            (Id: Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f12345678901"), Name: "Pink Floyd")
        };

        var payload = CatalogPayload.FromItems(CatalogType.Artist, items, (_, _) => new List<string> { "synonym" }, "it-IT");

        Assert.Equal(2, payload.Values.Count);
        Assert.Equal("Queen", payload.Values[0].Name.Value);
        Assert.Equal("Pink Floyd", payload.Values[1].Name.Value);
    }

    [Fact]
    public void FromItems_SkipsEmptyNames()
    {
        var items = new[]
        {
            (Id: Guid.NewGuid(), Name: "Queen"),
            (Id: Guid.NewGuid(), Name: ""),
            (Id: Guid.NewGuid(), Name: "   "),
            (Id: Guid.NewGuid(), Name: null!)
        };

        var payload = CatalogPayload.FromItems(CatalogType.Artist, items, (_, _) => new List<string>(), "it-IT");
        Assert.Single(payload.Values);
        Assert.Equal("Queen", payload.Values[0].Name.Value);
    }

    [Fact]
    public void FromItems_SetsSynonymsWhenGenerated()
    {
        var items = new[] { (Id: Guid.NewGuid(), Name: "Queen") };
        var payload = CatalogPayload.FromItems(CatalogType.Artist, items, (_, _) => new List<string> { "kuin" }, "it-IT");

        Assert.NotNull(payload.Values[0].Name.Synonyms);
        Assert.Equal(["kuin"], payload.Values[0].Name.Synonyms);
    }

    [Fact]
    public void FromItems_SetsSynonymsToNullWhenEmpty()
    {
        var items = new[] { (Id: Guid.NewGuid(), Name: "Queen") };
        var payload = CatalogPayload.FromItems(CatalogType.Artist, items, (_, _) => new List<string>(), "it-IT");

        Assert.Null(payload.Values[0].Name.Synonyms);
    }

    [Fact]
    public void FromItems_UsesSynonymGeneratorForEachItem()
    {
        int callCount = 0;
        var items = new[]
        {
            (Id: Guid.NewGuid(), Name: "A"),
            (Id: Guid.NewGuid(), Name: "B"),
            (Id: Guid.NewGuid(), Name: "C")
        };

        CatalogPayload.FromItems(CatalogType.Album, items, (name, _) =>
        {
            callCount++;
            return new List<string> { name.ToLowerInvariant() };
        }, "it-IT");

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void FromItems_EmptyInput_ReturnsEmptyPayload()
    {
        var payload = CatalogPayload.FromItems(CatalogType.Artist, [], (_, _) => new List<string>(), "it-IT");
        Assert.Empty(payload.Values);
    }

    [Fact]
    public void CatalogValue_SerializesToJsonCorrectly()
    {
        var value = new CatalogValue
        {
            Id = "jellyfin_artist_abc123",
            Name = new CatalogValueName
            {
                Value = "Queen",
                Synonyms = new List<string> { "kuin" }
            }
        };

        string json = JsonSerializer.Serialize(value);
        Assert.Contains("\"id\":\"jellyfin_artist_abc123\"", json);
        Assert.Contains("\"value\":\"Queen\"", json);
        Assert.Contains("\"synonyms\":[\"kuin\"]", json);
    }

    [Fact]
    public void CatalogValueName_SynonymsNull_SerializedAsNullByDefault()
    {
        var value = new CatalogValue
        {
            Id = "test",
            Name = new CatalogValueName { Value = "Test", Synonyms = null }
        };

        string json = JsonSerializer.Serialize(value);
        // Default System.Text.Json includes null properties
        Assert.Contains("synonyms", json);
        Assert.Contains("null", json);
    }

    [Fact]
    public void CatalogPayload_SerializesWithValuesArray()
    {
        var payload = new CatalogPayload
        {
            Values = new List<CatalogValue>
            {
                new() { Id = "id1", Name = new CatalogValueName { Value = "One" } },
                new() { Id = "id2", Name = new CatalogValueName { Value = "Two" } }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(2, values.GetArrayLength());
    }
}
