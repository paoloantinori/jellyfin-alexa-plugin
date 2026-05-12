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

    // --- Suggestion Threshold Tests ---

    [Fact]
    public void SuggestionThreshold_IsLowerThanDefaultThreshold()
    {
        Assert.True(FuzzyMatcher.SuggestionThreshold < FuzzyMatcher.DefaultThreshold);
    }

    [Fact]
    public void FindBestMatchWithScore_ExactMatch_ReturnsScore100()
    {
        var items = new List<TestItem> { new("The Beatles"), new("Metallica") };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("The Beatles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Value.Item.Name);
        Assert.Equal(100, result.Value.Score);
    }

    [Fact]
    public void FindBestMatchWithScore_Typo_ReturnsHighScore()
    {
        var items = new List<TestItem> { new("The Beatles") };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("Beatls", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Value.Item.Name);
        Assert.True(result.Value.Score >= FuzzyMatcher.DefaultThreshold);
    }

    [Fact]
    public void FindBestMatchWithScore_PoorMatch_ReturnsLowScore()
    {
        var items = new List<TestItem> { new("Metallica"), new("AC/DC") };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("xyzabc", items, i => i.Name);

        Assert.NotNull(result);
        Assert.True(result.Value.Score < FuzzyMatcher.SuggestionThreshold);
    }

    [Fact]
    public void FindBestMatchWithScore_EmptyQuery_ReturnsNull()
    {
        var items = new List<TestItem> { new("Test") };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("", items, i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatchWithScore_EmptyCandidates_ReturnsNull()
    {
        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("test", Enumerable.Empty<TestItem>(), i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatchWithScore_ReturnsBestAcrossMultiple()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("The Beetles"), // closer typo
            new("Metallica")
        };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("Beatls", items, i => i.Name);

        Assert.NotNull(result);
        Assert.True(result.Value.Score >= FuzzyMatcher.SuggestionThreshold);
    }

    // --- Scoring Zone Tests ---

    [Fact]
    public void ScoringZones_ConfidentMatch_PlaysDirectly()
    {
        // "beetles" should score >= 60 against "The Beatles" → confident match
        var items = new List<TestItem> { new("The Beatles") };
        var result = FuzzyMatcher.FindBestMatchWithScore("beetles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.True(result.Value.Score >= FuzzyMatcher.DefaultThreshold);
    }

    [Fact]
    public void ScoringZones_NoMatch_ReturnsLowScore()
    {
        // Completely unrelated strings should score below suggestion threshold
        var items = new List<TestItem> { new("Metallica") };
        var result = FuzzyMatcher.FindBestMatchWithScore("banana", items, i => i.Name);

        Assert.NotNull(result);
        Assert.True(result.Value.Score < FuzzyMatcher.SuggestionThreshold);
    }

    private record TestItem(string Name);
}
