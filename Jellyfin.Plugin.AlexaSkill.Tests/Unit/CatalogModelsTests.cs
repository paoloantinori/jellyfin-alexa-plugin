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
    public void FromItems_DeduplicatesSameTitledItems_FirstOccurrenceWins()
    {
        // JF-825: the audiobook shape that bites - one book held BOTH as a
        // single-file AudioBook leaf and as a chaptered AudioBook folder (and,
        // for artists/albums, the same-titled item in two libraries). Two
        // same-titled values with different jellyfin_audiobook_ ids make entity
        // resolution pick one arbitrarily, so FromItems dedups by the value's
        // canonical name (the truncated value, case-insensitive, the same key
        // CatalogSeedEnrichment.MergeSeeds compares) and the FIRST occurrence
        // wins: fetch order is the library priority order, and the survivor
        // keeps the first item's real Jellyfin id.
        var singleFileEdition = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var chapteredEdition = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var caseVariantEdition = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var items = new[]
        {
            (Id: singleFileEdition, Name: "Sapiens"),
            (Id: chapteredEdition, Name: "Sapiens"),
            (Id: caseVariantEdition, Name: "sapiens"),
            (Id: Guid.Parse("44444444-4444-4444-4444-444444444444"), Name: "Zero to One")
        };

        var payload = CatalogPayload.FromItems(CatalogType.Audiobook, items, (_, _) => new List<string>(), "it-IT");

        Assert.Equal(2, payload.Values.Count);
        Assert.Equal("Sapiens", payload.Values[0].Name.Value);
        Assert.Equal(
            CatalogValue.FormatId(CatalogType.Audiobook, singleFileEdition),
            payload.Values[0].Id);
        Assert.Equal("Zero to One", payload.Values[1].Name.Value);
    }

    [Fact]
    public void FromItems_DeduplicatesWhitespacePaddedDuplicates()
    {
        // JF-825 code-review F3: scraped metadata can pad a name ('Sapiens '
        // with a trailing space), and Truncate does not trim, so the key must:
        // the ER read side (SlotValueHelper.GetCanonicalValues) already dedups
        // authority names by Trim, and shipping both padded and unpadded forms
        // as two values resurrects the arbitrary-ER-pick this dedup exists to
        // remove.
        var cleanEdition = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var paddedEdition = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var items = new[]
        {
            (Id: cleanEdition, Name: "Sapiens"),
            (Id: paddedEdition, Name: "Sapiens ")
        };

        var payload = CatalogPayload.FromItems(CatalogType.Audiobook, items, (_, _) => new List<string>(), "it-IT");

        Assert.Single(payload.Values);
        Assert.Equal(
            CatalogValue.FormatId(CatalogType.Audiobook, cleanEdition),
            payload.Values[0].Id);
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
