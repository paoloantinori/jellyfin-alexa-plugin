using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class FuzzyMatcherTests
{
    [Fact]
    public void FindBestMatch_ExactMatch_ReturnsMatch()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("The Rolling Stones"),
            new("Led Zeppelin")
        };

        TestItem? result = FuzzyMatcher.FindBestMatch("The Beatles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Name);
    }

    [Fact]
    public void FindBestMatch_Typo_ReturnsMatch()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("The Rolling Stones"),
            new("Led Zeppelin")
        };

        TestItem? result = FuzzyMatcher.FindBestMatch("beetles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Name);
    }

    [Fact]
    public void FindBestMatch_PartialMatch_ReturnsMatch()
    {
        var items = new List<TestItem>
        {
            new("Led Zeppelin"),
            new("Metallica"),
            new("AC/DC")
        };

        TestItem? result = FuzzyMatcher.FindBestMatch("zepln", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("Led Zeppelin", result.Name);
    }

    [Fact]
    public void FindBestMatch_NoMatchAboveThreshold_ReturnsNull()
    {
        var items = new List<TestItem>
        {
            new("Metallica"),
            new("AC/DC"),
            new("Nirvana")
        };

        TestItem? result = FuzzyMatcher.FindBestMatch("xyzabc123", items, i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_EmptyQuery_ReturnsNull()
    {
        var items = new List<TestItem> { new("Test") };

        TestItem? result = FuzzyMatcher.FindBestMatch("", items, i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_EmptyCandidates_ReturnsNull()
    {
        TestItem? result = FuzzyMatcher.FindBestMatch("test", Enumerable.Empty<TestItem>(), i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void RankMatches_ReturnsSortedResults()
    {
        var items = new List<TestItem>
        {
            new("Metallica"),
            new("Metal"),
            new("The Beatles"),
            new("Metallurgic")
        };

        List<TestItem> results = FuzzyMatcher.RankMatches("metal", items, i => i.Name);

        Assert.NotEmpty(results);
        Assert.True(results.Count >= 2);
    }

    [Fact]
    public void PartialRatio_IdenticalStrings_Returns100()
    {
        int score = FuzzyMatcher.PartialRatio("hello", "hello");

        Assert.Equal(100, score);
    }

    [Fact]
    public void PartialRatio_Contains_Returns90()
    {
        int score = FuzzyMatcher.PartialRatio("beat", "The Beatles");

        Assert.Equal(90, score);
    }

    [Fact]
    public void LevenshteinDistance_IdenticalStrings_Returns0()
    {
        int distance = FuzzyMatcher.LevenshteinDistance("hello", "hello");

        Assert.Equal(0, distance);
    }

    [Fact]
    public void LevenshteinDistance_SingleEdit_Returns1()
    {
        int distance = FuzzyMatcher.LevenshteinDistance("cat", "bat");

        Assert.Equal(1, distance);
    }

    private record TestItem(string Name);
}
