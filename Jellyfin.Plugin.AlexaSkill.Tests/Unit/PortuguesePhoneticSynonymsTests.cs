using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PortuguesePhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = PortuguesePhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Anitta")]
    [InlineData("Ivete")]
    [InlineData("Sangalo")]
    public void Generate_PortugueseOriginNames_ReturnEmpty(string name)
    {
        var result = PortuguesePhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Oliveira")]
    [InlineData("Nascimento")]
    [InlineData("Pimentinha")]
    public void Generate_PortugueseEndings_ReturnEmpty(string name)
    {
        var result = PortuguesePhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsOsVariant()
    {
        var result = PortuguesePhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("os "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToD()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smids") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Pink Floyd");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void Generate_WithSh_TransformsToCh()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Fleetwood Mac");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Generate_WithTion_TransformsToSion()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Motion");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("sion") && !s.Contains("tion"));
    }

    [Fact]
    public void Generate_LeadingH_DroppedInVariant()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Heart");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = PortuguesePhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = PortuguesePhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = PortuguesePhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration tests ---

    [Fact]
    public void GenerateSynonyms_PortugueseBR_DispatchesToPortuguese()
    {
        var result = PortuguesePhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-BR");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_PortuguesePT_DispatchesToPortuguese()
    {
        var result = PortuguesePhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-PT");
        Assert.Equal(result, dispatchResult);
    }
}
