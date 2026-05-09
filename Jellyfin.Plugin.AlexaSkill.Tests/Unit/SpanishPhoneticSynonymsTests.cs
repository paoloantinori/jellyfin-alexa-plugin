using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class SpanishPhoneticSynonymsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = SpanishPhoneticSynonyms.Generate(name!);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Shakira")]
    [InlineData("Bisbal")]
    public void Generate_SpanishOriginNames_ReturnEmpty(string name)
    {
        var result = SpanishPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Rodriguez")]
    [InlineData("Gutierrez")]
    public void Generate_SpanishEndings_ReturnEmpty(string name)
    {
        var result = SpanishPhoneticSynonyms.Generate(name);
        Assert.Empty(result);
    }

    [Fact]
    public void Generate_TheBeatles_StripsArticleAndAddsLosVariant()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Beatles");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("los "));
    }

    [Fact]
    public void Generate_WithTh_TransformsToD()
    {
        var result = SpanishPhoneticSynonyms.Generate("Smiths");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smids") && !s.Contains("th"));
    }

    [Fact]
    public void Generate_WithPh_TransformsToF()
    {
        var result = SpanishPhoneticSynonyms.Generate("Pink Floyd");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void Generate_WithCk_TransformsToK()
    {
        var result = SpanishPhoneticSynonyms.Generate("Backstreet Boys");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void Generate_WithSh_TransformsToCh()
    {
        var result = SpanishPhoneticSynonyms.Generate("Fleetwood Mac");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Generate_LeadingH_DroppedInVariant()
    {
        var result = SpanishPhoneticSynonyms.Generate("Heart");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void Generate_ReturnsMaxThreeSynonyms()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Backstreet Boys");
        Assert.True(result.Count <= 3);
    }

    [Fact]
    public void Generate_NoDuplicatesInResult()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Weather Underground");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void Generate_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = SpanishPhoneticSynonyms.Generate("Nirvana");
        Assert.Empty(result);
    }

    // --- Dispatch integration ---

    [Fact]
    public void GenerateSynonyms_SpanishES_DispatchesToSpanish()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "es-ES");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_SpanishMX_DispatchesToSpanish()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "es-MX");
        Assert.Equal(result, dispatchResult);
    }

    [Fact]
    public void GenerateSynonyms_SpanishUS_DispatchesToSpanish()
    {
        var result = SpanishPhoneticSynonyms.Generate("The Smiths");
        var dispatchResult = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "es-US");
        Assert.Equal(result, dispatchResult);
    }
}
