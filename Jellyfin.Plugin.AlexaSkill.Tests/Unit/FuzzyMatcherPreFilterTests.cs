using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class FuzzyMatcherPreFilterTests
{
    [Fact]
    public void FindBestMatchWithScore_ExactMatch_ReturnsImmediately()
    {
        var items = CreateCandidateList(100, "The Beatles");
        items.Insert(0, new TestItem("The Beatles"));

        var result = FuzzyMatcher.FindBestMatchWithScore("The Beatles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Value.Item.Name);
        Assert.Equal(100, result.Value.Score);
    }

    [Fact]
    public void FindBestMatchWithScore_ContainmentMatch_ReturnsImmediately()
    {
        var items = CreateCandidateList(100, " filler name that will never match");
        items.Insert(0, new TestItem("The Beatles"));

        var result = FuzzyMatcher.FindBestMatchWithScore("Beatles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Value.Item.Name);
        Assert.Equal(FuzzyMatcher.ContainmentScore, result.Value.Score);
    }

    [Fact]
    public void FindBestMatchWithScore_LengthPreFilter_SkipsDistantCandidates()
    {
        var items = new List<TestItem>
        {
            new("A very extremely long artist name that goes on and on and on and on and on"),
            new("ABBA")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("ABBA", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("ABBA", result.Value.Item.Name);
        Assert.Equal(100, result.Value.Score);
    }

    [Fact]
    public void FindBestMatchWithScore_PartialMatchStillWorks_AsrTruncation()
    {
        var items = new List<TestItem>
        {
            new("Led Zeppelin"),
            new("Metallica"),
            new("AC/DC")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("led zep", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("Led Zeppelin", result.Value.Item.Name);
    }

    [Fact]
    public void FindBestMatchWithScore_TypoStillWorks()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("The Rolling Stones"),
            new("Led Zeppelin")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("beetles", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Value.Item.Name);
    }

    [Fact]
    public void FindBestMatchWithScore_AllExistingPatterns_WorkCorrectly()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("Pink Floyd"),
            new("Led Zeppelin"),
            new("Metallica"),
            new("AC/DC"),
            new("Nirvana")
        };

        var beatles = FuzzyMatcher.FindBestMatchWithScore("The Beatles", items, i => i.Name);
        Assert.NotNull(beatles);
        Assert.Equal(100, beatles.Value.Score);

        var pinkCont = FuzzyMatcher.FindBestMatchWithScore("pink", items, i => i.Name);
        Assert.NotNull(pinkCont);
        Assert.Equal(FuzzyMatcher.ContainmentScore, pinkCont.Value.Score);

        var typo = FuzzyMatcher.FindBestMatchWithScore("beetles", items, i => i.Name);
        Assert.NotNull(typo);
        Assert.Equal("The Beatles", typo.Value.Item.Name);
        Assert.True(typo.Value.Score >= FuzzyMatcher.DefaultThreshold);

        var partial = FuzzyMatcher.FindBestMatchWithScore("zep", items, i => i.Name);
        Assert.NotNull(partial);
        Assert.Equal("Led Zeppelin", partial.Value.Item.Name);
    }

    [Fact]
    public void FindBestMatchWithScore_ShortQuery_DoesNotSkipReasonableCandidates()
    {
        var items = new List<TestItem>
        {
            new("AB"),
            new("ABCDEF")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("AB", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("AB", result.Value.Item.Name);
        Assert.Equal(100, result.Value.Score);
    }

    [Fact]
    public void FindBestMatchWithScore_EmptyQuery_ReturnsNull()
    {
        var items = new List<TestItem> { new("Test") };

        var result = FuzzyMatcher.FindBestMatchWithScore("", items, i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatchWithScore_EmptyCandidates_ReturnsNull()
    {
        var result = FuzzyMatcher.FindBestMatchWithScore("test", Enumerable.Empty<TestItem>(), i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void RankMatches_ExactMatch_ReturnsSingleResultImmediately()
    {
        var items = new List<TestItem>
        {
            new("The Beatles"),
            new("The Rolling Stones"),
            new("Led Zeppelin"),
            new("Metallica")
        };

        List<TestItem> results = FuzzyMatcher.RankMatches("The Beatles", items, i => i.Name);

        Assert.Single(results);
        Assert.Equal("The Beatles", results[0].Name);
    }

    [Fact]
    public void RankMatches_NoExactMatch_ReturnsAllAboveThreshold()
    {
        var items = new List<TestItem>
        {
            new("Metallica"),
            new("The Beatles"),
            new("Metallurgic")
        };

        List<TestItem> results = FuzzyMatcher.RankMatches("metal", items, i => i.Name);

        Assert.NotEmpty(results);
        Assert.True(results.Count >= 2);
    }

    [Fact]
    public void FindBestMatchWithScore_LengthFilter_AllowsReasonableVariance()
    {
        var items = new List<TestItem>
        {
            new("Radiohead"),
            new("Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("Radio", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("Radiohead", result.Value.Item.Name);
    }

    [Fact]
    public void FindBestMatchWithScore_MultiWordQuery_WorksCorrectly()
    {
        var items = new List<TestItem>
        {
            new("Pink Floyd"),
            new("Pink"),
            new("Floyd Mayweather")
        };

        var result = FuzzyMatcher.FindBestMatchWithScore("pink floyd", items, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("Pink Floyd", result.Value.Item.Name);
        Assert.Equal(100, result.Value.Score);
    }

    private static List<TestItem> CreateCandidateList(int count, string suffix)
    {
        return Enumerable.Range(0, count)
            .Select(i => new TestItem($"candidate_{i}{suffix}"))
            .ToList();
    }

    private record TestItem(string Name);

    // --- JF-381: phonetic code-match preferred over substring containment for accent drift ---

    [Fact]
    public void FindBestMatch_Phonetic_MatchesCupToKoop_JF381()
    {
        var koopId = System.Guid.NewGuid();
        var items = new List<PhoneticCandidate>
        {
            new(koopId, "Koop"),
            new(System.Guid.NewGuid(), "Radiohead"),
            new(System.Guid.NewGuid(), "Metallica")
        };

        var result = FuzzyMatcher.FindBestMatch(
            "cup", items, c => c.Name, c => c.Id,
            id => id == koopId ? DoubleMetaphone.Encode("Koop") : null);

        Assert.NotNull(result);
        Assert.Equal("Koop", result!.Name);
    }

    [Fact]
    public void FindBestMatch_Phonetic_PrefersKoopOverPorcupineTree_JF381()
    {
        // THE REGRESSION GUARD: "Porcupine Tree" contains "cup" (containment score 90)
        // and shares the KP code. "Koop" also codes KP and is length-matched (4 vs 3).
        // The phonetic overload must prefer Koop (91, phonetic-floored) over Porcupine
        // Tree (90, coincidental substring containment).
        var koopId = System.Guid.NewGuid();
        var porcupineId = System.Guid.NewGuid();
        var items = new List<PhoneticCandidate>
        {
            new(koopId, "Koop"),
            new(porcupineId, "Porcupine Tree"),
            new(System.Guid.NewGuid(), "Radiohead")
        };

        var result = FuzzyMatcher.FindBestMatch(
            "cup", items, c => c.Name, c => c.Id,
            id => id == koopId ? DoubleMetaphone.Encode("Koop")
               : id == porcupineId ? DoubleMetaphone.Encode("Porcupine Tree") : null);

        Assert.NotNull(result);
        Assert.Equal("Koop", result!.Name);
    }

    [Fact]
    public void FindBestMatch_LevenshteinOnly_DoesNotMatchCupToKoop_JF381()
    {
        var items = new List<TestItem> { new("Koop"), new("Radiohead") };
        var result = FuzzyMatcher.FindBestMatch("cup", items, i => i.Name);
        Assert.Null(result);
    }

    private record PhoneticCandidate(System.Guid Id, string Name);
}
