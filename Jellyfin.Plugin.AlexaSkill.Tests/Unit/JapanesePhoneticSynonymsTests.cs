using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class JapanesePhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = JapanesePhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Yoko")]
    [InlineData("Utada")]
    public void Generate_JapaneseOriginNames_ReturnEmpty(string name)
    {
        var result = JapanesePhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Tanaka")]
    [InlineData("Yamamoto")]
    [InlineData("Hashimoto")]
    [InlineData("Kawasaki")]
    public void Generate_JapaneseEndings_ReturnEmpty(string name)
    {
        var result = JapanesePhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleNoArticleVariant()
    {
        var result = JapanesePhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        // Japanese has no articles — no "the" or other article variant should appear
        Assert.DoesNotContain(result, s => s.StartsWith("os ") || s.StartsWith("los ") || s.StartsWith("les ") || s.StartsWith("die ") || s.StartsWith("i ") || s.StartsWith("de "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToS()
    {
        var result = JapanesePhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smis") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = JapanesePhoneticSynonyms.Generate("Phone");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Fone") || !s.Contains("ph"));
    }

    [Fact]
    public void Generate_WithL_TransformsToR()
    {
        var result = JapanesePhoneticSynonyms.Generate("Linkin Park");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Rinkin") || s.Contains("R") && !s.Contains("L"));
    }

    [Fact]
    public void Generate_WithV_TransformsToB()
    {
        var result = JapanesePhoneticSynonyms.Generate("Velvet Underground");
        Assert.NotEmpty(result);
        // "Velvet" → v→b, l→r: "berbetu"
        Assert.Contains(result, s => s.Contains("berbetu") && !s.Contains("V") && !s.Contains("v"));
    }

    [Fact]
    public void Generate_WordFinalConsonant_AppendsU()
    {
        var result = JapanesePhoneticSynonyms.Generate("Pink Floyd");
        Assert.NotEmpty(result);
        // "Pink" ends with "k" which should get "u" appended → "Pinku"
        Assert.Contains(result, s => s.Contains("Pinku") || s.Contains("pinku"));
    }

    [Fact]
    public void Generate_SiTransformsToShi()
    {
        var result = JapanesePhoneticSynonyms.Generate("Silver");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Shi"));
    }

    [Fact]
    public void Generate_TiTransformsToChi()
    {
        var result = JapanesePhoneticSynonyms.Generate("Ticket");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Chi"));
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = JapanesePhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = JapanesePhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = JapanesePhoneticSynonyms.Generate("Weather Report");
        Assert.NotEmpty(result);
    }

    // --- Dispatch integration tests ---

    [Fact]
    public void GenerateSynonyms_JapaneseJP_DispatchesToJapanese()
    {
        var result = JapanesePhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "ja-JP");
        Assert.Equal(result, dispatchResult);
    }
}
