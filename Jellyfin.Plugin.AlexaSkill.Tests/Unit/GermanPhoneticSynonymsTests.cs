using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class GermanPhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = GermanPhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Rammstein")]
    [InlineData("Kraftwerk")]
    [InlineData("Scorpions")]
    [InlineData("Falco")]
    [InlineData("Nena")]
    public void Generate_GermanOriginNames_ReturnEmpty(string name)
    {
        var result = GermanPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Goldstein")]
    [InlineData("Hoffmann")]
    [InlineData("Rosenberg")]
    public void Generate_GermanEndings_ReturnEmpty(string name)
    {
        var result = GermanPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsDieVariant()
    {
        var result = GermanPhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("die "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToS()
    {
        var result = GermanPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smi") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithW_TransformsToV()
    {
        var result = GermanPhoneticSynonyms.Generate("White Stripes");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('v') || s.Contains('V'));
    }

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = GermanPhoneticSynonyms.Generate("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void Generate_LeadingH_DroppedInVariant()
    {
        var result = GermanPhoneticSynonyms.Generate("Heart");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = GermanPhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = GermanPhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = GermanPhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = GermanPhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration tests ---

    [Fact]
    public void GenerateSynonyms_GermanLocale_DispatchesToGerman()
    {
        var result = GermanPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "de-DE");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_GermanATLocale_DispatchesToGerman()
    {
        var result = GermanPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "de-AT");
        Assert.Equal(result, dispatchResult);
    }
}
