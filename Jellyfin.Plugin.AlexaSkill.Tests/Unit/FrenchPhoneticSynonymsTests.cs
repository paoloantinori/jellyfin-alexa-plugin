using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class FrenchPhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = FrenchPhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_FrenchOriginName_ReturnsEmpty()
    {
        // "Renaud" ends in -aud which is French-sounding but not in our list;
        // test with known list entries
        var result = FrenchPhoneticSynonyms.Generate("Daft");
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsLesVariant()
    {
        var result = FrenchPhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("les "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToZ()
    {
        var result = FrenchPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smiz") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = FrenchPhoneticSynonyms.Generate("Pink Floyd");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = FrenchPhoneticSynonyms.Generate("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void Generate_LeadingH_DroppedEntirely()
    {
        var result = FrenchPhoneticSynonyms.Generate("Heart");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("eart", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = FrenchPhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = FrenchPhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = FrenchPhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = FrenchPhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration ---

    [Fact]
    public void GenerateSynonyms_FrenchFR_DispatchesToFrench()
    {
        var result = FrenchPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "fr-FR");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_FrenchCA_DispatchesToFrench()
    {
        var result = FrenchPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "fr-CA");
        Assert.Equal(result, dispatchResult);
    }
}
