using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-426: a leading Italian nominative article must be stripped from raw musician
/// slot values (live probe 2026-09-13: "suona i 24 grana" delivered 'i 24 grana'
/// with the article intact when the artist is out of Amazon's catalog; the article
/// poisons every search tier).
/// </summary>
public class ArtistSearchArticleStripTests
{
    [Theory]
    [InlineData("i 24 grana", "24 grana")]
    [InlineData("gli afterhours", "afterhours")]
    [InlineData("il convict", "convict")]
    [InlineData("lamina", "lamina")] // no space: unchanged
    [InlineData("Pink Floyd", "Pink Floyd")] // leading word is not an article
    [InlineData("i", "i")] // article alone: unchanged (single token)
    public void StripLeadingArticle_ItIt(string raw, string expected)
        => Assert.Equal(expected, ArtistSearch.StripLeadingArticle(raw, "it-IT"));

    [Fact]
    public void StripLeadingArticle_NonItalianLocale_Unchanged()
        => Assert.Equal("i 24 grana", ArtistSearch.StripLeadingArticle("i 24 grana", "en-US"));

    [Fact]
    public void StripLeadingArticle_OnlyFirstArticleStripped()
        => Assert.Equal("gli stadio", ArtistSearch.StripLeadingArticle("gli gli stadio", "it-IT"));
}
