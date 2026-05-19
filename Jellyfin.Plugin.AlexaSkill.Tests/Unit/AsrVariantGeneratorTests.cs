using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class AsrVariantGeneratorTests
{
    [Fact]
    public void GenerateAsrVariants_TwoWords_ReturnsSingleCollapsedVariant()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants("lazy bones");

        var variant = Assert.Single(result);
        Assert.Equal("lazybones", variant);
    }

    [Fact]
    public void GenerateAsrVariants_FiveWords_ReturnsPairwisePlusCollapsed()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants("lazy bones in the morning");

        Assert.Equal(5, result.Count);
        Assert.Equal("lazybones in the morning", result[0]);
        Assert.Equal("lazy bonesin the morning", result[1]);
        Assert.Equal("lazy bones inthe morning", result[2]);
        Assert.Equal("lazy bones in themorning", result[3]);
        Assert.Equal("lazybonesinthemorning", result[4]);
    }

    [Fact]
    public void GenerateAsrVariants_SingleWord_ReturnsEmpty()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants("lazybones");

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateAsrVariants_EmptyString_ReturnsEmpty()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants(string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateAsrVariants_Null_ReturnsEmpty()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants(null);

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateAsrVariants_WhitespaceOnly_ReturnsEmpty()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants("   ");

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateAsrVariants_TwoWords_DeduplicatesPairwiseAndCollapsed()
    {
        // "lazy bones" pairwise join = "lazybones", collapsed = "lazybones"
        // Should appear only once
        var result = AsrVariantGenerator.GenerateAsrVariants("lazy bones");

        Assert.Single(result);
    }

    [Fact]
    public void GenerateAsrVariants_ThreeWords_ReturnsThreeVariants()
    {
        // "a b c" -> pairwise: "ab c", "a bc"; collapsed: "abc"
        var result = AsrVariantGenerator.GenerateAsrVariants("a b c");

        Assert.Equal(3, result.Count);
        Assert.Equal("ab c", result[0]);
        Assert.Equal("a bc", result[1]);
        Assert.Equal("abc", result[2]);
    }

    [Fact]
    public void GenerateAsrVariants_ResultIsReadOnlyList()
    {
        var result = AsrVariantGenerator.GenerateAsrVariants("lazy bones");

        Assert.IsAssignableFrom<IReadOnlyList<string>>(result);
    }

    [Fact]
    public void GenerateAsrVariants_ExtraWhitespace_NormalizesWords()
    {
        // Multiple spaces between words should still split correctly
        var result = AsrVariantGenerator.GenerateAsrVariants("lazy   bones");

        var variant = Assert.Single(result);
        Assert.Equal("lazybones", variant);
    }
}
