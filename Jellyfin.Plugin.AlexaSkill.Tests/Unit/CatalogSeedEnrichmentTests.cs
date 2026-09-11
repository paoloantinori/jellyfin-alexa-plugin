#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for the catalog seed enrichment (JF-541 phase 2): the static seed values
/// of the catalog-backed slot types (AlbumName, JellyfinArtist) are merged into
/// the SMAPI catalog upload, deduplicated with library entries winning, sourced
/// from the embedded interaction model JSONs.
/// </summary>
public class CatalogSeedEnrichmentTests
{
    private const int MaxSlotValueLength = 140;

    private static readonly Guid LibraryAlbumId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static List<string> NoSynonyms(string _, string __) => new();

    private static List<string> SingleSynonym(string name, string _) => new() { "syn-" + name };

    private static CatalogPayload BuildLibraryPayload(params string[] names)
    {
        var items = names.Select(n => (LibraryAlbumId, n));
        return CatalogPayload.FromItems(CatalogType.Album, items, NoSynonyms, "it-IT");
    }

    // ---- ExtractSeedNames ----

    [Fact]
    public void ExtractSeedNames_RawLanguageModelRoot_ExtractsValues()
    {
        const string json = """
        {
          "languageModel": {
            "types": [
              { "name": "Other", "values": [{ "name": { "value": "ignored" } }] },
              { "name": "AlbumName", "values": [
                { "name": { "value": "Thriller" } },
                { "name": { "value": "Abbey Road" } }
              ] }
            ]
          }
        }
        """;

        var seeds = CatalogSeedEnrichment.ExtractSeedNames(json, "AlbumName");

        Assert.Equal(new[] { "Thriller", "Abbey Road" }, seeds);
    }

    [Fact]
    public void ExtractSeedNames_SmapiEnvelope_ExtractsValues()
    {
        const string json = """
        {
          "interactionModel": {
            "languageModel": {
              "types": [
                { "name": "JellyfinArtist", "values": [{ "name": { "value": "Queen" } }] }
              ]
            }
          }
        }
        """;

        var seeds = CatalogSeedEnrichment.ExtractSeedNames(json, "JellyfinArtist");

        Assert.Equal(new[] { "Queen" }, seeds);
    }

    [Fact]
    public void ExtractSeedNames_MissingType_ReturnsEmpty()
    {
        const string json = """{ "languageModel": { "types": [{ "name": "Mood", "values": [] }] } }""";

        Assert.Empty(CatalogSeedEnrichment.ExtractSeedNames(json, "AlbumName"));
    }

    [Fact]
    public void ExtractSeedNames_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(CatalogSeedEnrichment.ExtractSeedNames("{ not json", "AlbumName"));
    }

    [Fact]
    public void ExtractSeedNames_BlankValuesAndCaseDuplicates_Filtered()
    {
        const string json = """
        {
          "languageModel": {
            "types": [
              { "name": "AlbumName", "values": [
                { "name": { "value": "Thriller" } },
                { "name": { "value": "  " } },
                { "name": { "value": "thriller" } }
              ] }
            ]
          }
        }
        """;

        var seeds = CatalogSeedEnrichment.ExtractSeedNames(json, "AlbumName");

        Assert.Equal(new[] { "Thriller" }, seeds);
    }

    // ---- MergeSeeds / MergeInto ----

    [Fact]
    public void MergeSeeds_AppendsSeedsWithStableIds()
    {
        var payload = BuildLibraryPayload("Some Local Album");
        int before = payload.Values.Count;

        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Album, new[] { "Thriller", "Abbey Road" }, SingleSynonym, "it-IT");

        Assert.Equal(before + 2, payload.Values.Count);

        CatalogValue thriller = payload.Values.Single(v => v.Name.Value == "Thriller");
        Assert.Equal(CatalogValue.FormatId(CatalogType.Album, CatalogSeedEnrichment.StableSeedGuid("Thriller")), thriller.Id);
        Assert.NotNull(thriller.Name.Synonyms);
        Assert.Equal("syn-Thriller", thriller.Name.Synonyms![0]);

        // deterministic: a second merge run for the same seed would derive the same id
        Assert.Equal(CatalogSeedEnrichment.StableSeedGuid("Thriller"), CatalogSeedEnrichment.StableSeedGuid("Thriller"));
        Assert.NotEqual(CatalogSeedEnrichment.StableSeedGuid("Thriller"), CatalogSeedEnrichment.StableSeedGuid("Abbey Road"));
    }

    [Fact]
    public void MergeSeeds_LibraryWinsOnCollision()
    {
        var payload = BuildLibraryPayload("Thriller");
        string libraryId = CatalogValue.FormatId(CatalogType.Album, LibraryAlbumId);

        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Album, new[] { "Thriller", "thriller", "Abbey Road" }, NoSynonyms, "it-IT");

        // Only the non-colliding seed is appended; the library entry keeps its real id.
        Assert.Equal(2, payload.Values.Count);
        CatalogValue libraryEntry = payload.Values.Single(v => v.Name.Value == "Thriller");
        Assert.Equal(libraryId, libraryEntry.Id);
        Assert.Equal("Abbey Road", payload.Values.Last().Name.Value);
    }

    [Fact]
    public void MergeSeeds_DeduplicatesAmongSeeds()
    {
        var payload = BuildLibraryPayload();

        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Album, new[] { "Rumours", "rumours" }, NoSynonyms, "it-IT");

        Assert.Single(payload.Values);
        Assert.Equal("Rumours", payload.Values[0].Name.Value);
    }

    [Fact]
    public void MergeSeeds_TruncatesLongSeedValueAndSynonyms()
    {
        var payload = BuildLibraryPayload();
        string longName = new string('a', 200);

        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Album, new[] { longName }, SingleSynonym, "it-IT");

        Assert.Single(payload.Values);
        Assert.True(payload.Values[0].Name.Value.Length <= MaxSlotValueLength);
        Assert.True(payload.Values[0].Name.Synonyms!.All(s => s.Length <= MaxSlotValueLength));
    }

    [Fact]
    public void MergeInto_TypeWithoutSeeds_IsNoOp()
    {
        var payload = BuildLibraryPayload("Local Album");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Series, NoSynonyms, "it-IT");
        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Song, NoSynonyms, "it-IT");

        Assert.Single(payload.Values);
    }

    // ---- Embedded-model sourcing (the real seeds) ----

    [Fact]
    public void GetSeedNames_Album_SourceIsItItModel()
    {
        var seeds = CatalogSeedEnrichment.GetSeedNames(CatalogType.Album);

        // The committed it-IT model's AlbumName block: the exact set is pinned so
        // a template edit that drifts the seeds fails here.
        Assert.Equal(
            new[]
            {
                "A Night at the Opera", "Abbey Road", "Back in Black", "Come mai",
                "Discovery", "La cura", "Rumours", "The Dark Side of the Moon", "Thriller"
            },
            seeds.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void GetSeedNames_Artist_UnionAcrossLocaleModels()
    {
        var seeds = CatalogSeedEnrichment.GetSeedNames(CatalogType.Artist);

        // it-IT (8 seeds incl. the Italian artists) unioned with the 10 en-* seeds.
        Assert.Subset(
            new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Queen", "Pink Floyd", "Radiohead", "The Beatles", "Michael Jackson",
                "The Police", "Maneskin", "Ligabue", "The Rolling Stones", "Metallica",
                "Coldplay", "Miles Davis"
            });
        Assert.Equal(12, seeds.Count);
    }

    [Fact]
    public void MergeInto_AlbumPayload_GainsThrillerWhenNotInLibrary()
    {
        var payload = BuildLibraryPayload("Some Local Album");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Album, NoSynonyms, "it-IT");

        Assert.Contains(payload.Values, v => v.Name.Value == "Thriller");
        Assert.Contains(payload.Values, v => v.Name.Value == "Some Local Album");
        Assert.Equal(1 + CatalogSeedEnrichment.GetSeedNames(CatalogType.Album).Count, payload.Values.Count);
    }

    [Fact]
    public void MergeInto_AlbumPayload_LibraryThrillerSuppressesSeed()
    {
        var payload = BuildLibraryPayload("Thriller");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Album, NoSynonyms, "it-IT");

        Assert.Single(payload.Values.Where(v => v.Name.Value == "Thriller"));
        Assert.Equal(
            CatalogValue.FormatId(CatalogType.Album, LibraryAlbumId),
            payload.Values.Single(v => v.Name.Value == "Thriller").Id);
    }
}
