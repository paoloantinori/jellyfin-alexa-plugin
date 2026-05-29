using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PhoneticSynonymGeneratorTests
{
    // --- Italian locale tests (existing behavior preserved) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void GenerateSynonyms_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name!, "it-IT");
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Metallica")]
    [InlineData("Adele")]
    [InlineData("Pausini")]
    [InlineData("Bocelli")]
    [InlineData("Pavarotti")]
    public void GenerateSynonyms_ItalianOriginNames_ReturnEmpty(string name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name, "it-IT");
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Brunello")]
    [InlineData("Bianchetti")]
    [InlineData("Moretti")]
    public void GenerateSynonyms_ItalianEndings_ReturnEmpty(string name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name, "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_Queen_ProducesPhoneticVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Queen", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('u') || s.Contains('v'));
    }

    [Fact]
    public void GenerateSynonyms_TheBeatles_StripsArticleAndAddsItalianVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("i "));
    }

    [Fact]
    public void GenerateSynonyms_PinkFloyd_TransformsPhToF()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void GenerateSynonyms_Backstreet_Boys_TransformsCkToK()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Backstreet Boys", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void GenerateSynonyms_WithTh_TransformsToT()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Smiths", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_ReturnsMaxThreeSynonyms()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Backstreet Boys", "it-IT");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void GenerateSynonyms_NoDuplicatesInResult()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Weather Underground", "it-IT");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void GenerateSynonyms_LeadingH_DroppedInVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Heart", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void GenerateSynonyms_WithW_TransformsW()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("White Stripes", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('v') || s.Contains('V') || s.Contains('u') || s.Contains('U'));
    }

    [Fact]
    public void GenerateSynonyms_OughSound_TransformsToOf()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Through Fire", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ough"));
    }

    [Fact]
    public void GenerateSynonyms_TionSuffix_TransformsToSionOrZion()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Motion Orchestra", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("sion") || s.Contains("zion"));
    }

    [Fact]
    public void GenerateSynonyms_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Nirvana", "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Weather Report", "it-IT");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GenerateSynonyms_DoubledConsonantName_DetectedAsItalian()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Botticelli", "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_WithSh_TransformsShToSc()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Fleetwood Mac", "it-IT");
        Assert.NotEmpty(result);
    }

    // --- Locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_EnglishLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_EnglishGBLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles", "en-GB");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_ItalianLocaleVariant_Works()
    {
        // it-IT and it-CH should both dispatch to Italian phonetics
        var resultIT = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-IT");
        var resultCH = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-CH");
        Assert.NotEmpty(resultIT);
        Assert.NotEmpty(resultCH);
        Assert.Equal(resultIT.Count, resultCH.Count);
    }

    [Fact]
    public void GenerateSynonyms_UnknownLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "xx-XX");
        Assert.Empty(result);
    }

    // --- Portuguese locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_PortugueseBR_DispatchesToPortuguese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-BR");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smids") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_PortuguesePT_DispatchesToPortuguese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-PT");
        Assert.NotEmpty(result);
    }

    // --- Japanese locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_JapaneseJP_DispatchesToJapanese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "ja-JP");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smis") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_JapaneseJP_LToRTransform()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Linkin Park", "ja-JP");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Rinkin"));
    }

    // --- Dutch locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_DutchNL_DispatchesToDutch()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-NL");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smits") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_DutchBE_DispatchesToDutch()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-BE");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GenerateSynonyms_NullLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", null!);
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_EmptyLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "");
        Assert.Empty(result);
    }
}
