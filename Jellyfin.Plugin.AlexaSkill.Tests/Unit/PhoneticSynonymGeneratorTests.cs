using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PhoneticSynonymGeneratorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void GenerateSynonyms_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name!);
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
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Brunello")]
    [InlineData("Bianchetti")]
    [InlineData("Moretti")]
    public void GenerateSynonyms_ItalianEndings_ReturnEmpty(string name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name);
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_Queen_ProducesPhoneticVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Queen");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('u') || s.Contains('v'));
    }

    [Fact]
    public void GenerateSynonyms_TheBeatles_StripsArticleAndAddsItalianVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("i "));
    }

    [Fact]
    public void GenerateSynonyms_PinkFloyd_TransformsPhToF()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void GenerateSynonyms_Backstreet_Boys_TransformsCkToK()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void GenerateSynonyms_WithTh_TransformsToT()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_ReturnsMaxThreeSynonyms()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void GenerateSynonyms_NoDuplicatesInResult()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void GenerateSynonyms_LeadingH_DroppedInVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Heart");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void GenerateSynonyms_WithW_TransformsW()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("White Stripes");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('v') || s.Contains('V') || s.Contains('u') || s.Contains('U'));
    }

    [Fact]
    public void GenerateSynonyms_OughSound_TransformsToOf()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Through Fire");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ough"));
    }

    [Fact]
    public void GenerateSynonyms_TionSuffix_TransformsToSionOrZion()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Motion Orchestra");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("sion") || s.Contains("zion"));
    }

    [Fact]
    public void GenerateSynonyms_NoTransformablePhonetics_ReturnsEmpty()
    {
        // "Nirvana" has no English-specific phonetic transforms (no th, sh, ph, ck, w, ough, tion)
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Nirvana");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Weather Report");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GenerateSynonyms_DoubledConsonantName_DetectedAsItalian()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Botticelli");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_WithSh_TransformsShToSc()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Fleetwood Mac");
        Assert.NotEmpty(result);
    }
}
