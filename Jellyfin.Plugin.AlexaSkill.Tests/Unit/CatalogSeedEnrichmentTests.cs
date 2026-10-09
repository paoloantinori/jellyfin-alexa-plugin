#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
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
        => BuildLibraryPayload(CatalogType.Album, "it-IT", names);

    private static CatalogPayload BuildLibraryPayload(CatalogType type, string locale, params string[] names)
    {
        var items = names.Select(n => (LibraryAlbumId, n));
        return CatalogPayload.FromItems(type, items, NoSynonyms, locale);
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

    // ---- Audiobook seed arm (JF-823) ----

    /// <summary>
    /// JF-823: the it-IT AudiobookTitle seed (8 Italian classics + the 14
    /// English titles of the JF-816 residual fix) is the fallback vocabulary
    /// for book titles the user's library does not carry. Catalog wiring is
    /// replace-in-place, so WITHOUT this arm the first sync after wiring
    /// REPLACES the live model's 22-value block with library-only values and
    /// the seed vanishes; the exact set is pinned so a template edit that
    /// drifts the seeds fails here.
    /// </summary>
    [Fact]
    public void GetSeedNames_Audiobook_SourceIsItItModel()
    {
        var seeds = CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook);

        Assert.Equal(
            new[]
            {
                "Atomic Habits", "Born a Crime", "Cent'anni di Solitudine", "Deep Work",
                "Educated", "Harry Potter e la Pietra Filosofale", "Homo Deus",
                "Il Gattopardo", "Il Nome della Rosa", "Il Piccolo Principe",
                "Il Signore degli Anelli", "La Coscienza di Zeno", "Measure What Matters",
                "Predictably Irrational", "Sapiens", "Se Questo È un Uomo",
                "Steve Jobs", "The Power of Habit", "The Psychology of Money",
                "The Upside of Irrationality", "Thinking Fast and Slow", "Zero to One"
            },
            seeds.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Seed survival: the merged payload the catalog upload serves must carry
    /// a seed title the library does NOT hold, so the replace-in-place wiring
    /// (CatalogWiringGraft.Apply -> InjectCatalogReferences) cannot strip it
    /// from the live vocabulary at first sync.
    /// </summary>
    [Fact]
    public void MergeInto_AudiobookPayload_GainsItItSeedWhenNotInLibrary()
    {
        var payload = CatalogPayload.FromItems(
            CatalogType.Audiobook,
            new[] { (LibraryAlbumId, "A Library-Only Book") },
            NoSynonyms,
            "it-IT");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "it-IT");

        Assert.Contains(payload.Values, v => v.Name.Value == "A Library-Only Book");
        Assert.Contains(payload.Values, v => v.Name.Value == "Il Piccolo Principe");
        Assert.Equal(
            1 + CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count,
            payload.Values.Count);
    }

    /// <summary>
    /// Library wins on collision, the shared MergeSeeds rule: a library book
    /// keeps its real Jellyfin id, and the seed entry is not duplicated.
    /// </summary>
    [Fact]
    public void MergeInto_AudiobookPayload_LibrarySapiensSuppressesSeed()
    {
        var payload = CatalogPayload.FromItems(
            CatalogType.Audiobook,
            new[] { (LibraryAlbumId, "Sapiens") },
            NoSynonyms,
            "it-IT");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "it-IT");

        Assert.Single(payload.Values.Where(v => v.Name.Value == "Sapiens"));
        Assert.Equal(
            CatalogValue.FormatId(CatalogType.Audiobook, LibraryAlbumId),
            payload.Values.Single(v => v.Name.Value == "Sapiens").Id);
    }

    // ---- Audiobook generic-word arm (JF-823 live A/B verdict, 2026-10-09) ----

    /// <summary>
    /// The 16 non-it locales' AudiobookTitle vocabulary was exactly ONE generic
    /// word pre-wiring; the replace-in-place catalog sync left them with
    /// library titles only, so bare generic-word requests degraded to
    /// NO_SELECTION live (de-DE "lies ein hörbuch", en-GB "play an audiobook").
    /// The arm: each locale's own word rides ITS OWN leg only, never a union.
    /// </summary>
    [Fact]
    public void MergeInto_AudiobookPayload_DeLegGainsItsOwnGenericWordOnly()
    {
        var payload = BuildLibraryPayload(CatalogType.Audiobook, "de-DE", "A Library-Only Book");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "de-DE");

        // The leg's own generic word rides the payload.
        Assert.Contains(payload.Values, v => v.Name.Value == "Hörbuch");

        // The OTHER locales' generic words do not (per-locale arm, not a union).
        Assert.DoesNotContain(payload.Values, v => v.Name.Value == "luisterboek");
        Assert.DoesNotContain(payload.Values, v => v.Name.Value == "livre audio");
        Assert.DoesNotContain(payload.Values, v => v.Name.Value == "audiolibro");
        Assert.DoesNotContain(payload.Values, v => v.Name.Value == "audiolivro");

        // The shared it-IT title seed still rides every leg, and the payload is
        // exactly library + shared seeds + the one generic word.
        Assert.Contains(payload.Values, v => v.Name.Value == "Il Piccolo Principe");
        Assert.Equal(
            2 + CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count,
            payload.Values.Count);
    }

    /// <summary>
    /// it-IT never carried a generic word (its AudiobookTitle block IS the
    /// title seed), so its leg is byte-identical to the pre-arm shape.
    /// </summary>
    [Fact]
    public void MergeInto_AudiobookPayload_ItItLegCarriesNoGenericWord()
    {
        var payload = BuildLibraryPayload(CatalogType.Audiobook, "it-IT", "A Library-Only Book");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "it-IT");

        Assert.Equal(
            1 + CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook).Count,
            payload.Values.Count);
    }

    /// <summary>
    /// Idempotent per sync: a second MergeInto for the same payload must not
    /// double-append the generic word (the shared MergeSeeds dedup).
    /// </summary>
    [Fact]
    public void MergeInto_AudiobookPayload_GenericWordDoesNotDoubleAppend()
    {
        var payload = BuildLibraryPayload(CatalogType.Audiobook, "en-GB", "A Library-Only Book");

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "en-GB");
        int afterFirst = payload.Values.Count;

        CatalogSeedEnrichment.MergeInto(payload, CatalogType.Audiobook, NoSynonyms, "en-GB");

        Assert.Equal(afterFirst, payload.Values.Count);
        Assert.Single(payload.Values.Where(v => v.Name.Value == "audiobook"));
    }

    /// <summary>
    /// The table is SOURCED from each non-it template's AudiobookTitle block
    /// (the live-verdict instruction: do not invent). This pin enforces the
    /// sourcing both ways: every non-it locale with an AudiobookTitle block has
    /// a table entry whose word equals the template's single value, so a
    /// template edit that changes a locale's generic word without the table
    /// update in the same change fails here (the JF-823 F5 drift-tripwire
    /// pattern: a pin failure here reads "template edit without its table
    /// update", not "JF-823 broke"). The slot type name comes from the same
    /// production map the arm's loader reads, so a rename drifts this pin
    /// together with production instead of hardcoding a stale literal.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestLocales.LocalesWithResourcePaths), MemberType = typeof(TestLocales))]
    public void GenericAudiobookWords_MatchEachTemplatesAudiobookTitleValue(string locale, string resourcePath)
    {
        if (locale == "it-IT")
        {
            return;
        }

        using var resource = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly.GetManifestResourceStream(resourcePath);
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource!);
        var values = CatalogSeedEnrichment.ExtractSeedNames(
            reader.ReadToEnd(),
            CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook]);

        Assert.True(
            CatalogSeedEnrichment.GenericAudiobookWords.TryGetValue(locale, out string? word),
            $"locale {locale} carries an AudiobookTitle block but has no generic-word table entry");
        Assert.Equal(new[] { word }, values);
    }

    /// <summary>
    /// No phantom entry: the table's key set is exactly the roster of non-it
    /// locales (the per-locale Theory above only visits locales that exist in
    /// the embedded models, so a typo'd or stale extra key would otherwise be
    /// invisible dead code).
    /// </summary>
    [Fact]
    public void GenericAudiobookWords_KeySetMirrorsTheNonItLocaleRoster()
    {
        var expected = TestLocales.AllLocales()
            .Where(l => !string.Equals(l, "it-IT", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, CatalogSeedEnrichment.GenericAudiobookWords.Keys.ToHashSet(StringComparer.Ordinal));
    }
}
