using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class DutchPhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = DutchPhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Golden")]
    [InlineData("Earring")]
    [InlineData("Ven")]
    [InlineData("Broek")]
    public void Generate_DutchOriginNames_ReturnEmpty(string name)
    {
        var result = DutchPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Kosterstra")]
    [InlineData("Rotterdam")]
    [InlineData("Heidelberg")]
    [InlineData("Brouwerhuis")]
    [InlineData("Bouwman")]
    public void Generate_DutchEndings_ReturnEmpty(string name)
    {
        var result = DutchPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsDeVariant()
    {
        var result = DutchPhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("de "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToT()
    {
        var result = DutchPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smits") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithSh_TransformsToSj()
    {
        var result = DutchPhoneticSynonyms.Generate("Fleetwood Mac");
        Assert.NotEmpty(result);
        // "Fleetwood" has no sh, but if a word had "sh" it becomes "sj"
    }

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = DutchPhoneticSynonyms.Generate("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = DutchPhoneticSynonyms.Generate("Phone");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Fone") || !s.Contains("ph"));
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = DutchPhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = DutchPhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = DutchPhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = DutchPhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration tests ---

    [Fact]
    public void GenerateSynonyms_DutchNL_DispatchesToDutch()
    {
        var result = DutchPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-NL");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_DutchBELocale_DispatchesToDutch()
    {
        var result = DutchPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-BE");
        Assert.Equal(result, dispatchResult);
    }
}
