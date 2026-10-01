using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-689: the one CatalogValue construction path. The byte-identity pins hold
/// both builders' output byte-for-byte against the pre-extraction inline
/// construction shape (the exact code both sites carried before JF-689,
/// including the JF-684 AppendTo call), so the extraction can never drift the
/// catalog payload bytes; the assembly-scan pin makes the one-path shape itself
/// machine-checked.
/// </summary>
public class CatalogValueFactoryTests
{
    [Fact]
    public void Factory_MatchesLegacyInlineConstruction_ByteForByte()
    {
        // Entry-level parity: enrichment lands (phonetic family + partial word),
        // normalization to null on an empty family, the gate-reject name, the
        // non-Artist no-op, and the 140-char truncation on value and synonyms.
        var guid = Guid.Parse("11111111111111111111111111111111");
        var entries = new[]
        {
            (CatalogType.Artist, guid, "Pink Floyd", new List<string> { "i Pink Floyd", "Pinc Floyd" }),
            (CatalogType.Artist, guid, "The Beatles", new List<string>()),
            (CatalogType.Artist, guid, "Led Zeppelin", new List<string>()),
            (CatalogType.Album, guid, "Thriller", new List<string> { "i Thriller" }),
            (CatalogType.Artist, guid, new string('a', 60) + " floyd " + new string('b', 100), new List<string> { new string('c', 150) })
        };

        foreach (var (type, itemId, name, synonyms) in entries)
        {
            CatalogValue built = CatalogValueFactory.Create(type, itemId, name, synonyms);

            string actual = JsonSerializer.Serialize(built);
            string expected = JsonSerializer.Serialize(LegacyConstruction(type, itemId, name, synonyms));
            Assert.True(actual == expected, $"Entry '{name}' drifted from the legacy construction:{actual}");
        }
    }

    [Fact]
    public void FromItems_MatchesLegacyPerEntryConstruction_ByteForByte()
    {
        // Payload-level parity for the library-items site, including the
        // whitespace-name skip and the value ordering.
        var names = new[]
        {
            "Pink Floyd", "The Beatles", "Norah Jones", "Led Zeppelin", "   ", "Koop"
        };
        var items = names.Select((n, i) => (Id: Guid.Parse($"{i:D8}-0000-0000-0000-000000000000"), Name: n)).ToArray();

        CatalogPayload payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, (_, _) => new List<string> { "i phonetic" }, "it-IT");

        var expected = new CatalogPayload();
        foreach ((Guid id, string name) in items)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            List<string> synonyms = new List<string> { "i phonetic" };
            expected.Values.Add(LegacyConstruction(CatalogType.Artist, id, name, synonyms));
        }

        Assert.Equal(
            JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void MergeSeeds_MatchesLegacyPerEntryConstruction_ByteForByte()
    {
        // Site-level parity for the seed site, symmetric with the FromItems pin:
        // MergeSeeds itself drives the factory, and the distinct seeds keep the
        // dedup gate inactive so every seed produces exactly one oracle entry.
        var seeds = new[] { "The Beatles", "Pink Floyd", "Norah Jones" };

        var payload = new CatalogPayload();
        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Artist, seeds, (_, _) => new List<string>(), "it-IT");

        var expected = new CatalogPayload();
        foreach (string seed in seeds)
        {
            expected.Values.Add(LegacyConstruction(
                CatalogType.Artist, CatalogSeedEnrichment.StableSeedGuid(seed), seed, new List<string>()));
        }

        Assert.Equal(
            JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void ArtistEnrichmentGuard_MissingPartialWord_Throws()
    {
        // The guard's own contract (code-review finding): every other pin
        // exercises only the passing side, so the throw path, the null-synonyms
        // branch, and the OrdinalIgnoreCase presence check are locked here.
        var missing = new CatalogValueName
        {
            Value = "Pink Floyd",
            Synonyms = new List<string> { "i Pink Floyd" }
        };
        Assert.Throws<InvalidOperationException>(
            () => CatalogValueFactory.AssertArtistEnrichment(missing));

        var noList = new CatalogValueName { Value = "The Beatles" };
        Assert.Throws<InvalidOperationException>(
            () => CatalogValueFactory.AssertArtistEnrichment(noList));

        var present = new CatalogValueName { Value = "Pink Floyd", Synonyms = new List<string> { "PINK" } };
        CatalogValueFactory.AssertArtistEnrichment(present);
    }

    [Fact]
    public void CatalogValue_HasNoConstructionSiteOutsideTheFactory()
    {
        // JF-689: the factory is the enrichment guard only while it is the ONE
        // construction path. Mirrors the WarmingGateCoverageTests IL-scan
        // discipline (shared IlCallScanner, newobj-only discovery, so reflection
        // and serializers are invisible by boundary): any production construction
        // of CatalogValue outside CatalogValueFactory would silently skip both
        // the JF-684 enrichment and the structural guard, and fails here with
        // the site named.
        Module module = typeof(CatalogValue).Module;

        var offenders = IlCallScanner.DeclaredMethods(typeof(CatalogValue).Assembly)
            .Where(pair => pair.Type != typeof(CatalogValueFactory))
            .Where(pair => IlCallScanner.ConstructsType(pair.Method, module, typeof(CatalogValue)))
            .Select(pair => $"{pair.Type.FullName}.{pair.Method.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "CatalogValue must be constructed only inside CatalogValueFactory (JF-689); "
            + $"construction sites found elsewhere: [{string.Join(", ", offenders)}]");
    }

    /// <summary>
    /// The exact construction both builders carried before JF-689 (truncation,
    /// synonym normalization, then the JF-684 AppendTo), kept verbatim as the
    /// byte-identity oracle for the factory. It must NEVER delegate to
    /// CatalogValueFactory: the oracle exists to catch factory drift, and
    /// delegating would make the pins tautological with the suite green.
    /// </summary>
    private static CatalogValue LegacyConstruction(CatalogType type, Guid itemId, string name, List<string> synonyms)
    {
        var catalogValue = new CatalogValue
        {
            Id = CatalogValue.FormatId(type, itemId),
            Name = new CatalogValueName
            {
                Value = SlotValueHelper.Truncate(name),
                Synonyms = synonyms.Count > 0 ? synonyms.Select(SlotValueHelper.Truncate).ToList() : null
            }
        };

        PartialNameSynonyms.AppendTo(catalogValue.Name, type);

        return catalogValue;
    }
}
