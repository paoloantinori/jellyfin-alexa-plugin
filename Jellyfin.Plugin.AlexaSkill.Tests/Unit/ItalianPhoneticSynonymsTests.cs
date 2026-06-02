using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class ItalianPhoneticSynonymsTests
{
    // --- Short / null / whitespace names ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = ItalianPhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    // --- Italian origin detection (skips Italian-sounding names) ---

    [Theory]
    [InlineData("Metallica")]
    [InlineData("Adele")]
    [InlineData("Pavarotti")]
    [InlineData("Bocelli")]
    [InlineData("Ramazzotti")]
    public void Generate_KnownItalianNames_ReturnsEmpty(string name)
    {
        var result = ItalianPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Cannelloni", "i", "ll")]  // ends in vowel 'i', has doubled 'll'
    [InlineData("Puccini", "i", "cc")]     // ends in vowel 'i', has doubled 'cc'
    [InlineData("Roselli", "i", "ll")]     // ends in vowel 'i', has doubled 'll'
    public void Generate_DoubledConsonantWithVowelEnding_ReturnsEmpty(string name, string ending, string doubled)
    {
        var result = ItalianPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Crescentello")]
    [InlineData("Bianchi")]
    [InlineData("Moretti")]
    public void Generate_ItalianSuffixName_ReturnsEmpty(string name)
    {
        var result = ItalianPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_MultiWordName_NotFilteredAsItalian()
    {
        // Multi-word names skip the Italian origin check (only single words are checked)
        var result = ItalianPhoneticSynonyms.Generate("Metallica Band");
        Assert.NotEmpty(result);
    }

    // --- th -> t ---

    [Fact]
    public void Generate_WithTh_TransformsToT()
    {
        var result = ItalianPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smits") && !s.Contains("th", StringComparison.OrdinalIgnoreCase));
    }

    // --- ph -> f ---

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = ItalianPhoneticSynonyms.Generate("Siphon");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Sifon") && !s.Contains("ph", StringComparison.OrdinalIgnoreCase));
    }

    // --- sh -> sc ---

    [Fact]
    public void Generate_WithSh_TransformsToSc()
    {
        var result = ItalianPhoneticSynonyms.Generate("Shake");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Scake", StringComparison.OrdinalIgnoreCase) && !s.Contains("sh", StringComparison.OrdinalIgnoreCase));
    }

    // --- ck -> k ---

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = ItalianPhoneticSynonyms.Generate("Backstreet");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Bakstreet") && !s.Contains("ck", StringComparison.OrdinalIgnoreCase));
    }

    // --- ough -> of ---

    [Fact]
    public void Generate_WithOugh_TransformsToOf()
    {
        var result = ItalianPhoneticSynonyms.Generate("Thought");
        Assert.NotEmpty(result);
        // "Thought" → "Thoft" (ough→of) → "toft" (Th→t)
        Assert.Contains(result, s => s.Equals("toft", StringComparison.OrdinalIgnoreCase));
    }

    // --- tion -> sion (primary) / zion (alternate) ---

    [Fact]
    public void Generate_WithTion_ProducesSionVariant()
    {
        var result = ItalianPhoneticSynonyms.Generate("Action");
        Assert.NotEmpty(result);
        // Primary: tion→sion → "Acsion"
        Assert.Contains(result, s => s.Contains("Acsion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WithTion_ProducesZionAlternateVariant()
    {
        var result = ItalianPhoneticSynonyms.Generate("Action");
        // Alternate: tion→zion → "Aczion"
        Assert.Contains(result, s => s.Contains("Aczion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WithTion_ProducesBothPrimaryAndAlternate()
    {
        var result = ItalianPhoneticSynonyms.Generate("Action");
        // Both variants should be present
        Assert.True(result.Count >= 2, "Expected at least 2 variants for 'Action'");
        Assert.Contains(result, s => s.Contains("sion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, s => s.Contains("zion", StringComparison.OrdinalIgnoreCase));
    }

    // --- Silent h at start ---

    [Fact]
    public void Generate_LeadingH_Dropped()
    {
        var result = ItalianPhoneticSynonyms.Generate("Heart");
        Assert.NotEmpty(result);
        // "Heart" → no phonetic transforms except silent h → "eart"
        Assert.Contains(result, s => s.StartsWith("eart", StringComparison.OrdinalIgnoreCase));
    }

    // --- w -> v before front vowels (e/i), w -> u otherwise (primary) ---

    [Fact]
    public void Generate_WBeforeFrontVowel_TransformsToV()
    {
        var result = ItalianPhoneticSynonyms.Generate("Winter");
        Assert.NotEmpty(result);
        // Primary: W before 'i' (front vowel) → V → "Vinter"
        Assert.Contains(result, s => s.Contains("Vinter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WBeforeNonFrontVowel_TransformsToU()
    {
        var result = ItalianPhoneticSynonyms.Generate("Water");
        Assert.NotEmpty(result);
        // Primary: W before 'a' (not front vowel) → U → "Uater"
        Assert.Contains(result, s => s.Contains("Uater", StringComparison.OrdinalIgnoreCase));
    }

    // --- w -> v always (alternate path) ---

    [Fact]
    public void Generate_AlternateWAlwaysV_ProducesVVariant()
    {
        var result = ItalianPhoneticSynonyms.Generate("Water");
        // Alternate: W → V always → "Vater"
        Assert.Contains(result, s => s.Contains("Vater", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_Water_HasBothPrimaryAndAlternateWVariants()
    {
        var result = ItalianPhoneticSynonyms.Generate("Water");
        // "Water" should produce "Uater" (primary) and "Vater" (alternate)
        Assert.True(result.Count >= 2, "Expected at least 2 variants for 'Water'");
        Assert.Contains(result, s => s.Contains("Uater", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, s => s.Contains("Vater", StringComparison.OrdinalIgnoreCase));
    }

    // --- "The" article stripping ---

    [Fact]
    public void Generate_TheSmiths_StripsArticleAndAddsIPrefix()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Smiths");
        Assert.NotEmpty(result);
        // Should contain a variant with "i " prefix (Italian plural article)
        Assert.Contains(result, s => s.StartsWith("i ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsIPrefix()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("i ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_TheBeatles_VariantDoesNotContainThe()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Beatles");
        // After stripping "The", variants should not contain "The "
        Assert.DoesNotContain(result, s => s.StartsWith("The ", StringComparison.OrdinalIgnoreCase));
    }

    // --- "i" prefix for band names (no "The") ---

    [Fact]
    public void Generate_BandNameWithoutThe_GetsIPrefix()
    {
        var result = ItalianPhoneticSynonyms.Generate("Red Hot Chili Peppers");
        Assert.NotEmpty(result);
        // Multi-word name without comma → LooksLikeBandName → "i" prefix variant
        Assert.Contains(result, s => s.StartsWith("i ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_SingleWordName_DoesNotGetIPrefix()
    {
        var result = ItalianPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        // Single word → not a band name → no "i" prefix
        Assert.DoesNotContain(result, s => s.StartsWith("i ", StringComparison.OrdinalIgnoreCase));
    }

    // --- Max 3 results ---

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    // --- No duplicates ---

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    // --- No transformable phonetics → returns empty ---

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = ItalianPhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    // --- Doubled consonant collapse (alternate path) ---

    [Fact]
    public void Generate_AlternatePath_CollapsesDoubledConsonants()
    {
        var result = ItalianPhoneticSynonyms.Generate("Bass");
        Assert.NotEmpty(result);
        // Primary: "Bass" has no transforms → same as original → not added
        // Alternate: "Bass" no transforms + CollapseDoubledConsonants → "Bas" → added
        Assert.Contains(result, s => s.Equals("Bas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_AlternatePath_CollapsesDoubledConsonantsInBandName()
    {
        var result = ItalianPhoneticSynonyms.Generate("Red Hot Chili Peppers");
        // Alternate collapses "Peppers" → "Pepers" (doubled p collapsed)
        Assert.Contains(result, s => s.Contains("Pepers", StringComparison.OrdinalIgnoreCase)
                                   && !s.Equals("i Red ot Chili Peppers", StringComparison.OrdinalIgnoreCase));
    }

    // --- Band with transformable features ---

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = ItalianPhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Compound transforms ---

    [Fact]
    public void Generate_NameWithMultipleTransforms_AppliesAll()
    {
        // "Backstreet" has "ck" → "k" transform
        // As a single word, it won't get "i" prefix
        var result = ItalianPhoneticSynonyms.Generate("Backstreet");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Bakstreet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WordWithOughAndTh_AppliesBothTransforms()
    {
        // "Thought" has both "ough" and "th"
        var result = ItalianPhoneticSynonyms.Generate("Thought");
        Assert.NotEmpty(result);
        // ough→of first: "Thoft", then th→t: "toft"
        Assert.Contains(result, s => s.Equals("toft", StringComparison.OrdinalIgnoreCase));
    }

    // --- Single word with "The" prefix strips it ---

    [Fact]
    public void Generate_ThePrefix_StripsAndTransforms()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Smiths");
        Assert.NotEmpty(result);
        // "Smiths" → "Smits" (th→t)
        Assert.Contains(result, s => s.Contains("Smits", StringComparison.OrdinalIgnoreCase)
                                   && !s.StartsWith("i ", StringComparison.OrdinalIgnoreCase));
    }

    // --- Three-character name (above the 2-char minimum) ---

    [Fact]
    public void Generate_ThreeCharName_Processed()
    {
        // "The" (3 chars, no trailing space) → th→t → "te"
        var result = ItalianPhoneticSynonyms.Generate("The");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration via PhoneticSynonymGenerator ---

    [Fact]
    public void GenerateSynonyms_ItIT_DispatchesToItalian()
    {
        var result = ItalianPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "it-IT");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_UnknownLocale_ReturnsEmpty()
    {
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "en-US");
        Assert.Empty(dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_NullLocale_ReturnsEmpty()
    {
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", null!);
        Assert.Empty(dispatchResult);
    }
}
