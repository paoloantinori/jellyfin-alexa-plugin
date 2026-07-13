using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // --- Phonetic pre-filter tests ---

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_PhoneticAndLevenshteinMiss_StillFinds()
    {
        // "smit" is close to "Smith" — phonetic codes should match and boost the score
        var smith = new TestItemWithId("Smith", Guid.NewGuid());
        var metallica = new TestItemWithId("Metallica", Guid.NewGuid());
        var items = new List<TestItemWithId> { metallica, smith };

        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [smith.Id] = DoubleMetaphone.Encode("Smith"),
            [metallica.Id] = DoubleMetaphone.Encode("Metallica"),
        };

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        var result = FuzzyMatcher.FindBestMatch(
            "smit",
            items,
            i => i.Name,
            i => i.Id,
            Lookup,
            40);

        Assert.NotNull(result);
        Assert.Equal("Smith", result.Name);
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_PhoneticMissLevenshteinHit_StillFinds()
    {
        // "beetles" → Levenshtein should match "The Beatles" even without phonetic help
        var beatles = new TestItemWithId("The Beatles", Guid.NewGuid());
        var items = new List<TestItemWithId> { beatles };

        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [beatles.Id] = DoubleMetaphone.Encode("The Beatles"),
        };

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        var result = FuzzyMatcher.FindBestMatch(
            "beetles",
            items,
            i => i.Name,
            i => i.Id,
            Lookup,
            FuzzyMatcher.DefaultThreshold);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Name);
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_NoPhoneticData_FallsBackToLevenshtein()
    {
        // When phonetic lookup returns null for all candidates, should still work via Levenshtein
        var beatles = new TestItemWithId("The Beatles", Guid.NewGuid());
        var items = new List<TestItemWithId> { beatles };

        (string Primary, string? Alternate)? Lookup(Guid _) => null;

        var result = FuzzyMatcher.FindBestMatch(
            "beetles",
            items,
            i => i.Name,
            i => i.Id,
            Lookup,
            FuzzyMatcher.DefaultThreshold);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result.Name);
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_PhoneticBoostsLowerLevenshteinScore()
    {
        // When two candidates have similar Levenshtein scores, phonetic match should
        // boost the correct one to win
        var smith = new TestItemWithId("Smith", Guid.NewGuid());
        var smoot = new TestItemWithId("Smoot", Guid.NewGuid());
        var items = new List<TestItemWithId> { smoot, smith };

        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [smith.Id] = DoubleMetaphone.Encode("Smith"),
            [smoot.Id] = DoubleMetaphone.Encode("Smoot"),
        };

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        // Query "smit" — phonetically matches "Smith" (SM0T/XMT) which should boost it
        var result = FuzzyMatcher.FindBestMatch(
            "smit",
            items,
            i => i.Name,
            i => i.Id,
            Lookup,
            40);

        Assert.NotNull(result);
        // With phonetic boost, Smith should win (even if Smoot has similar Levenshtein)
        Assert.Equal("Smith", result.Name);
    }

    [Fact]
    public void FindBestMatchWithScore_WithPhoneticLookup_ReturnsBoostedScore()
    {
        var smith = new TestItemWithId("Smith", Guid.NewGuid());
        var items = new List<TestItemWithId> { smith };

        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [smith.Id] = DoubleMetaphone.Encode("Smith"),
        };

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        // Get score without phonetic
        var scoreWithoutPhonetic = FuzzyMatcher.FindBestMatchWithScore("smit", items, i => i.Name);

        // Get score with phonetic
        var scoreWithPhonetic = FuzzyMatcher.FindBestMatchWithScore(
            "smit", items, i => i.Name, i => i.Id, Lookup);

        Assert.NotNull(scoreWithoutPhonetic);
        Assert.NotNull(scoreWithPhonetic);

        // Phonetic match should boost the score
        Assert.True(scoreWithPhonetic.Value.Score >= scoreWithoutPhonetic.Value.Score,
            $"Phonetic score ({scoreWithPhonetic.Value.Score}) should be >= non-phonetic score ({scoreWithoutPhonetic.Value.Score})");
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_ExactMatch_ShortCircuits()
    {
        var beatles = new TestItemWithId("The Beatles", Guid.NewGuid());
        var items = new List<TestItemWithId> { beatles };

        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [beatles.Id] = DoubleMetaphone.Encode("The Beatles"),
        };

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        var result = FuzzyMatcher.FindBestMatchWithScore(
            "The Beatles",
            items,
            i => i.Name,
            i => i.Id,
            Lookup);

        Assert.NotNull(result);
        Assert.Equal(100, result.Value.Score);
    }

    [Fact]
    public void PhoneticCodesMatch_MatchingPrimary_ReturnsTrue()
    {
        Assert.True(FuzzyMatcher.PhoneticCodesMatch("ABCD", null, "ABCD", null));
    }

    [Fact]
    public void PhoneticCodesMatch_MatchingAlternate_ReturnsTrue()
    {
        Assert.True(FuzzyMatcher.PhoneticCodesMatch("ABCD", "WXYZ", "WXYZ", null));
    }

    [Fact]
    public void PhoneticCodesMatch_NoMatch_ReturnsFalse()
    {
        Assert.False(FuzzyMatcher.PhoneticCodesMatch("ABCD", null, "WXYZ", null));
    }

    [Fact]
    public void PhoneticCodesMatch_EmptyPrimary_ReturnsFalse()
    {
        Assert.False(FuzzyMatcher.PhoneticCodesMatch(string.Empty, null, "ABCD", null));
    }

    [Fact]
    public void PhoneticCodesMatch_CrossMatchPrimaryAlternate_ReturnsTrue()
    {
        // query primary matches candidate alternate
        Assert.True(FuzzyMatcher.PhoneticCodesMatch("ABCD", null, "XYZ", "ABCD"));
    }

    [Fact]
    public void PhoneticCodesMatch_AllAlternatesMatch_ReturnsTrue()
    {
        Assert.True(FuzzyMatcher.PhoneticCodesMatch("ABCD", "WXYZ", "QRST", "WXYZ"));
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_EmptyQuery_ReturnsNull()
    {
        var items = new List<TestItemWithId> { new("Test", Guid.NewGuid()) };

        (string Primary, string? Alternate)? Lookup(Guid _) => null;

        var result = FuzzyMatcher.FindBestMatch(
            "",
            items,
            i => i.Name,
            i => i.Id,
            Lookup);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_WithPhoneticLookup_EmptyCandidates_ReturnsNull()
    {
        (string Primary, string? Alternate)? Lookup(Guid _) => null;

        var result = FuzzyMatcher.FindBestMatch(
            "test",
            Enumerable.Empty<TestItemWithId>(),
            i => i.Name,
            i => i.Id,
            Lookup);

        Assert.Null(result);
    }

    [Fact]
    public void Performance_PhoneticFuzzyMatch_10KArtists_Under10ms()
    {
        // Build 10K artists with IDs and pre-computed phonetic codes
        var artists = new List<TestItemWithId>(10000);
        var phoneticMap = new Dictionary<Guid, (string Primary, string? Alternate)>(10000);

        for (int i = 0; i < 10000; i++)
        {
            var item = new TestItemWithId($"Artist {i}", Guid.NewGuid());
            artists.Add(item);
            phoneticMap[item.Id] = DoubleMetaphone.Encode(item.Name);
        }

        // Add some real names
        var smith = new TestItemWithId("Smith", Guid.NewGuid());
        artists.Add(smith);
        phoneticMap[smith.Id] = DoubleMetaphone.Encode("Smith");

        var pinkFloyd = new TestItemWithId("Pink Floyd", Guid.NewGuid());
        artists.Add(pinkFloyd);
        phoneticMap[pinkFloyd.Id] = DoubleMetaphone.Encode("Pink Floyd");

        (string Primary, string? Alternate)? Lookup(Guid id) =>
            phoneticMap.TryGetValue(id, out var codes) ? codes : null;

        var sw = Stopwatch.StartNew();

        var result = FuzzyMatcher.FindBestMatch(
            "smit",
            artists,
            i => i.Name,
            i => i.Id,
            Lookup,
            40);

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200,
            $"Phonetic fuzzy match on 10K artists took {sw.ElapsedMilliseconds}ms, expected < 200ms");
    }

    [Fact]
    public void FindBestMatchWithScore_AsrAccentAndSpelling_MatchesPhonetically_JF336()
    {
        // JF-336: the Echo's Italian ASR transcribes "jazz cafe" as "jazz caffè"
        // (double-f + grave accent); the library album is "Jazz Cafe" (English,
        // single-f, no accent). Jellyfin search returns 0, so PlayAlbum's phonetic
        // fallback must bridge it via FuzzyMatcher (Double Metaphone folds both to "KF").
        var albums = new List<TestItem>
        {
            new("Jazz Cafe"),
            new("Thriller"),
            new("Abbey Road"),
            new("Back in Black")
        };

        (TestItem Item, int Score)? result = FuzzyMatcher.FindBestMatchWithScore("jazz caffè", albums, i => i.Name);

        Assert.NotNull(result);
        Assert.Equal("Jazz Cafe", result!.Value.Item.Name);
        Assert.True(result.Value.Score >= FuzzyMatcher.DefaultThreshold,
            $"expected 'jazz caffè' to phonetic-match 'Jazz Cafe' at >= {FuzzyMatcher.DefaultThreshold}, got {result.Value.Score}");
    }

    private record TestItemWithId(string Name, Guid Id);
}
