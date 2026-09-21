using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-610: the shared single-cut carrier primitive and the ONE locale-prefix
/// helper. Every case here mirrors a behavior the four former private copies
/// pinned incident-driven (JF-469 bare word, JF-600 trailing forms).
/// </summary>
public class CarrierPhraseAndLocalePrefixTests
{
    [Theory]
    [InlineData("chiamata prova echo", new[] { "chiamata " }, "prova echo")]
    [InlineData("the song called breathe", new[] { "the song called ", "the song " }, "breathe")]
    [InlineData("Chiamata Prova", new[] { "chiamata " }, "Prova")] // case-insensitive
    public void Leading_CutsOnce(string input, string[] carriers, string expected)
    {
        string value = input;
        Assert.True(CarrierPhrase.TryStripLeading(ref value, carriers));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("chiamata", new[] { "chiamata " })] // bare carrier word: no cut
    [InlineData("chiamataprova", new[] { "chiamata " })] // word fragment: no cut
    [InlineData("chiamato  ", new[] { "chiamata ", "chiamato " })] // carrier + only whitespace: no cut (the empty-SearchTerm guard)
    [InlineData("canzone ", new[] { "canzone " })] // exact space-baked carrier: no cut
    [InlineData("prova", new[] { "chiamata " })]
    public void Leading_NoCutShapes(string input, string[] carriers)
    {
        string value = input;
        Assert.False(CarrierPhrase.TryStripLeading(ref value, carriers));
        Assert.Equal(input, value);
    }

    [Fact]
    public void Trailing_CutsOnce()
    {
        string value = "テスト という";
        Assert.True(CarrierPhrase.TryStripTrailing(ref value, new[] { " という" }));
        Assert.Equal("テスト", value);
    }

    [Fact]
    public void Trailing_BareCarrier_NoCut()
    {
        string value = "という";
        Assert.False(CarrierPhrase.TryStripTrailing(ref value, new[] { " という" }));
        Assert.Equal("という", value);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("it-IT", "it")]
    [InlineData("pt-BR", "pt")]
    [InlineData("en", "en")] // dashless returns ITSELF (the JF-610 reconciliation)
    [InlineData(null, "")]
    [InlineData("", "")]
    public void LocalePrefix_Of(string locale, string expected)
    {
        Assert.Equal(expected, LocalePrefix.Of(locale));
    }
}
