using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-684: the partial-name synonym gate. Catalog-backed slot types gate NLU intent
/// selection (live A/B 2026-09-30, skill 33dfacd5: "suona la musica di pink" selected
/// NO intent until "pink" existed as a catalog synonym), so the accept/reject table
/// below is what keeps bare-word voice reachability open while function words stay
/// out of the catalog.
/// </summary>
public class PartialNameSynonymsTests
{
    // ---- Generate: the accept/reject table ----

    [Theory]
    [InlineData("Pink Floyd", "Pink")]           // first word of a multi-word name
    [InlineData("The Beatles", "Beatles")]       // leading English article skipped
    [InlineData("Crash Test Dummies", "Crash")]  // the ASR-truncation shape (JF-684 corpus)
    [InlineData("Norah Jones", "Norah")]
    [InlineData("Van Morrison", "Morrison")]     // leading nl stop word ("van") skipped
    [InlineData("Il Volo", "Volo")]              // leading it stop word ("il") skipped
    [InlineData("norah jones", "norah")]         // casing of the name's word is preserved
    public void Generate_MultiWordName_YieldsFirstSubstantiveWord(string name, string expected)
    {
        Assert.Equal(expected, PartialNameSynonyms.Generate(name));
    }

    [Theory]
    [InlineData("Led Zeppelin")]   // first substantive word below the length bar ("led")
    [InlineData("The Who")]        // word after the article below the bar ("Who")
    [InlineData("Koop")]           // single-word name: it is already its own value
    [InlineData("Da La Soul")]     // candidate after the skip is itself a stop word ("La")
    [InlineData("P!nk floyd")]     // >=4-char first word with punctuation: not bare-word material
    [InlineData("50 Cent")]        // non-letter first word (digit)
    [InlineData("5.6.7.8's")]      // single word, non-letter characters
    [InlineData("")]               // empty
    [InlineData("   ")]            // whitespace
    public void Generate_RejectedShapes_ReturnNull(string name)
    {
        Assert.Null(PartialNameSynonyms.Generate(name));
    }

    [Theory]
    [InlineData("the", true)]   // en
    [InlineData("van", true)]   // nl
    [InlineData("de", true)]    // nl/de
    [InlineData("che", true)]   // it
    [InlineData("pink", false)]
    [InlineData("crash", false)]
    [InlineData("beatles", false)]
    public void IsStopWordInAnyLocale_CoversAllLocaleSets(string word, bool expected)
    {
        Assert.Equal(expected, KeywordMatcher.IsStopWordInAnyLocale(word));
    }

    // ---- AppendTo: the payload-level enrichment ----

    [Fact]
    public void AppendTo_ArtistEntry_AppendsPartialWordAfterPhoneticFamily()
    {
        var name = new CatalogValueName
        {
            Value = "Pink Floyd",
            Synonyms = new List<string> { "i Pink Floyd", "Pinc Floyd" }
        };

        PartialNameSynonyms.AppendTo(name, CatalogType.Artist);

        Assert.Equal(new[] { "i Pink Floyd", "Pinc Floyd", "Pink" }, name.Synonyms);
    }

    [Fact]
    public void AppendTo_ArtistEntryWithoutSynonyms_CreatesSynonymList()
    {
        var name = new CatalogValueName { Value = "The Beatles", Synonyms = null };

        PartialNameSynonyms.AppendTo(name, CatalogType.Artist);

        Assert.NotNull(name.Synonyms);
        Assert.Equal(new[] { "Beatles" }, name.Synonyms);
    }

    [Fact]
    public void AppendTo_AlreadyPresent_IsNotDuplicated()
    {
        // Only the case VARIANT of the word: a case-sensitive dedup would append a
        // duplicate "Pink", so this pins the OrdinalIgnoreCase skip.
        var name = new CatalogValueName
        {
            Value = "Pink Floyd",
            Synonyms = new List<string> { "pink" }
        };

        PartialNameSynonyms.AppendTo(name, CatalogType.Artist);

        Assert.Equal(new[] { "pink" }, name.Synonyms);
    }

    [Fact]
    public void AppendTo_NonArtistCatalogTypes_AreNoOp()
    {
        // JF-684 scope: the musician slot only. Album first words stay untouched
        // (the AlbumName anchors already steal artist queries, JF-508 family).
        foreach (CatalogType type in Enum.GetValues(typeof(CatalogType)))
        {
            if (type == CatalogType.Artist)
            {
                continue;
            }

            var name = new CatalogValueName { Value = "Pink Floyd" };
            PartialNameSynonyms.AppendTo(name, type);
            Assert.Null(name.Synonyms);
        }
    }

    // ---- The generated catalog payload (the JF-684 live-corpus shapes) ----

    [Fact]
    public void FromItems_ArtistCatalog_PinkFloydCarriesPinkSynonym()
    {
        var items = new[] { (Id: Guid.NewGuid(), Name: "Pink Floyd") };

        var payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, (_, _) => new List<string> { "i Pink Floyd" }, "it-IT");

        CatalogValueName entry = payload.Values.Single().Name;
        Assert.Equal("Pink Floyd", entry.Value);
        Assert.Contains("Pink", entry.Synonyms!);
    }

    [Fact]
    public void FromItems_ArtistCatalog_SharedFirstWordAddedToBothEntries()
    {
        // The task's multi-artist case: two artists share the first word. BOTH get it;
        // the multi-value ER match is then decided by Amazon's ranking (GetCanonicalValue
        // returns Values[0], which feeds the search verbatim, JF-659; the JF-420.1 exact
        // bypass auto-plays it; the prompt does not fire on this path, see JF-690). The
        // live spike proved exactly this shape: "pink" matched P!nk AND Pink Floyd.
        var items = new[]
        {
            (Id: Guid.NewGuid(), Name: "Miles Davis"),
            (Id: Guid.NewGuid(), Name: "Miles Kane")
        };

        var payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, (_, _) => new List<string>(), "it-IT");

        Assert.All(payload.Values, v => Assert.Contains("Miles", v.Name.Synonyms!));
    }

    [Fact]
    public void FromItems_AlbumCatalog_NoPartialSynonym()
    {
        var items = new[] { (Id: Guid.NewGuid(), Name: "Pink Floyd") };

        var payload = CatalogPayload.FromItems(
            CatalogType.Album, items, (_, _) => new List<string>(), "it-IT");

        Assert.Null(payload.Values.Single().Name.Synonyms);
    }

    [Fact]
    public void MergeSeeds_ArtistSeed_CarriesPartialSynonym()
    {
        var payload = new CatalogPayload();

        CatalogSeedEnrichment.MergeSeeds(
            payload, CatalogType.Artist, new[] { "The Beatles" }, (_, _) => new List<string>(), "it-IT");

        CatalogValueName entry = payload.Values.Single(v => v.Name.Value == "The Beatles").Name;
        Assert.Contains("Beatles", entry.Synonyms!);
    }
}
