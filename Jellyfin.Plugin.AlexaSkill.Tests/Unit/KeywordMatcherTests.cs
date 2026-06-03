#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class KeywordMatcherTests
{
    // ─── Tokenize: Stop Words ───────────────────────────────────────────

    [Fact]
    public void Tokenize_StripsEnglishStopWords()
    {
        var result = KeywordMatcher.Tokenize("the boy in the bubble", "en-US");
        Assert.Equal(new[] { "boy", "bubble" }, result);
    }

    [Fact]
    public void Tokenize_StripsItalianStopWords()
    {
        var result = KeywordMatcher.Tokenize("il sole e la luna", "it-IT");
        Assert.Equal(new[] { "sole", "luna" }, result);
    }

    [Fact]
    public void Tokenize_StripsGermanStopWords()
    {
        var result = KeywordMatcher.Tokenize("der mond und die sterne", "de-DE");
        Assert.Equal(new[] { "mond", "sterne" }, result);
    }

    [Fact]
    public void Tokenize_StripsFrenchStopWords()
    {
        var result = KeywordMatcher.Tokenize("le chat et le chien", "fr-FR");
        Assert.Equal(new[] { "chat", "chien" }, result);
    }

    [Fact]
    public void Tokenize_StripsFrenchCanadianStopWords()
    {
        var result = KeywordMatcher.Tokenize("une chanson sur la vie", "fr-CA");
        Assert.Equal(new[] { "chanson", "vie" }, result);
    }

    // ─── Tokenize: Punctuation ──────────────────────────────────────────

    [Fact]
    public void Tokenize_SplitsOnParentheses()
    {
        var result = KeywordMatcher.Tokenize("Bohemian Rhapsody (Remastered)", "en-US");
        Assert.Equal(new[] { "bohemian", "rhapsody", "remastered" }, result);
    }

    [Fact]
    public void Tokenize_SplitsOnDashes()
    {
        var result = KeywordMatcher.Tokenize("Rock-N-Roll", "en-US");
        Assert.Equal(new[] { "rock", "n", "roll" }, result);
    }

    [Fact]
    public void Tokenize_SplitsOnApostrophes()
    {
        var result = KeywordMatcher.Tokenize("don't stop believin'", "en-US");
        Assert.Equal(new[] { "don", "t", "stop", "believin" }, result);
    }

    [Fact]
    public void Tokenize_SplitsOnMultiplePunctuation()
    {
        var result = KeywordMatcher.Tokenize("hey! jude? yeah, right.", "en-US");
        Assert.Equal(new[] { "hey", "jude", "yeah", "right" }, result);
    }

    // ─── Tokenize: Lowercase ────────────────────────────────────────────

    [Fact]
    public void Tokenize_LowercasesOutput()
    {
        var result = KeywordMatcher.Tokenize("Bohemian Rhapsody", "en-US");
        Assert.Equal(new[] { "bohemian", "rhapsody" }, result);
    }

    [Fact]
    public void Tokenize_MixedCaseInput()
    {
        var result = KeywordMatcher.Tokenize("StAiNwAy To HeAvEn", "en-US");
        Assert.Equal(new[] { "stainway", "heaven" }, result);
    }

    // ─── Tokenize: Empty / Whitespace ───────────────────────────────────

    [Fact]
    public void Tokenize_NullInput_ReturnsEmptyArray()
    {
        var result = KeywordMatcher.Tokenize(null, "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptyArray()
    {
        var result = KeywordMatcher.Tokenize(string.Empty, "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_ReturnsEmptyArray()
    {
        var result = KeywordMatcher.Tokenize("   \t\n  ", "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_StopWordsOnly_ReturnsEmptyArray()
    {
        var result = KeywordMatcher.Tokenize("the a an of in", "en-US");
        Assert.Empty(result);
    }

    // ─── Tokenize: Preservation ─────────────────────────────────────────

    [Fact]
    public void Tokenize_PreservesNonStopWords()
    {
        var result = KeywordMatcher.Tokenize("hotel california", "en-US");
        Assert.Equal(new[] { "hotel", "california" }, result);
    }

    [Fact]
    public void Tokenize_PreservesNumbers()
    {
        var result = KeywordMatcher.Tokenize("song 2 blur", "en-US");
        Assert.Equal(new[] { "song", "2", "blur" }, result);
    }

    [Fact]
    public void Tokenize_UnknownLocale_NoStopWordsRemoved()
    {
        // "ja" has no stop word set, so all words are preserved
        var result = KeywordMatcher.Tokenize("watashi no uta", "ja-JP");
        Assert.Equal(new[] { "watashi", "no", "uta" }, result);
    }

    [Fact]
    public void Tokenize_EmptyLocale_NoStopWordsRemoved()
    {
        var result = KeywordMatcher.Tokenize("the a song", string.Empty);
        Assert.Equal(new[] { "the", "a", "song" }, result);
    }

    // ─── Tokenize: Edge Cases ───────────────────────────────────────────

    [Fact]
    public void Tokenize_VeryLongInput()
    {
        var words = Enumerable.Range(0, 200).Select(i => $"word{i}").ToArray();
        var input = string.Join(" ", words);
        var result = KeywordMatcher.Tokenize(input, "en-US");
        Assert.Equal(200, result.Length);
        Assert.Equal("word0", result[0]);
        Assert.Equal("word199", result[199]);
    }

    [Fact]
    public void Tokenize_SingleStopWord_ReturnsEmpty()
    {
        var result = KeywordMatcher.Tokenize("the", "en-US");
        Assert.Empty(result);
    }

    // ─── Score: Keyword Coverage (all must match) ───────────────────────

    [Fact]
    public void Score_AllKeywordsMustMatch_SongMissingAKeywordIsExcluded()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() },
            new() { Name = "Bohemian Dreams", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("bohemian rhapsody", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        // "Bohemian Dreams" lacks "rhapsody" -> excluded
        Assert.Single(result);
        Assert.Equal("Bohemian Rhapsody", result[0].Item.Name);
    }

    [Fact]
    public void Score_TwoKeywordsOneMatches_Excluded()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Dreams", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("dreams fleetwood", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Empty(result);
    }

    // ─── Score: Formula ─────────────────────────────────────────────────

    [Fact]
    public void Score_SingleKeywordSingleWordTitle_ScoreIs100()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Hello", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("hello", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        // keywordCoverage=1.0, titleCoverage=1.0 -> (0.7*1 + 0.3*1)*100 = 100, positional +5 = 105
        Assert.Equal(105.0, result[0].Score, 1);
    }

    [Fact]
    public void Score_TwoKeywordsBothMatchButPartialTitleCoverage_ScoreLessThan100()
    {
        // Title: "hotel california live" -> tokens: hotel, california, live
        // Keywords: "hotel california" -> keywordCoverage=1.0, titleCoverage=2/3
        var songs = new List<Audio>
        {
            new() { Name = "Hotel California Live", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("hotel california", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        // score = (0.7*1.0 + 0.3*(2/3)) * 100 = (0.7 + 0.2) * 100 = 90.0
        // positional bonus: "hotel" matches from first title token -> +5
        Assert.Equal(95.0, result[0].Score, 1);
    }

    [Fact]
    public void Score_ScoreFormulaWithoutPositionalBonus()
    {
        // Title: "live hotel california" -> tokens: live, hotel, california
        // Keywords: "hotel california" -> keywordCoverage=1.0, titleCoverage=2/3
        // Positional: first title token "live" is NOT in keywords -> no bonus
        var songs = new List<Audio>
        {
            new() { Name = "Live Hotel California", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("hotel california", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        // score = (0.7*1.0 + 0.3*(2/3)) * 100 = 90.0, no positional bonus
        Assert.Equal(90.0, result[0].Score, 1);
    }

    // ─── Score: Positional Bonus ────────────────────────────────────────

    [Fact]
    public void Score_PositionalBonus_WhenKeywordsMatchFromFirstToken()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("bohemian", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        // keywordCoverage=1.0, titleCoverage=1/2 -> (0.7 + 0.15)*100 = 85.0
        // positional bonus +5 = 90.0
        Assert.Equal(90.0, result[0].Score);
    }

    [Fact]
    public void Score_NoPositionalBonus_WhenFirstTokenDoesNotMatchKeywords()
    {
        // Title: "the bohemian rhapsody" -> tokens: bohemian, rhapsody (after stop word removal)
        // Keywords: "rhapsody" -> keywordCoverage=1.0, titleCoverage=1/2
        // Positional: first token "bohemian" is NOT in keywords -> no bonus
        var songs = new List<Audio>
        {
            new() { Name = "the bohemian rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("rhapsody", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        // score = (0.7*1.0 + 0.3*0.5)*100 = (0.7 + 0.15)*100 = 85.0, no bonus
        Assert.Equal(85.0, result[0].Score);
    }

    // ─── Score: Sorting ─────────────────────────────────────────────────

    [Fact]
    public void Score_ResultsSortedByScoreDescending()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Live Hotel California", Id = Guid.NewGuid() },
            new() { Name = "Hotel California", Id = Guid.NewGuid() },
            new() { Name = "California Hotel Remix", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("hotel california", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        // All three should match (both keywords present in each)
        Assert.Equal(3, result.Count);
        // Should be sorted by score descending
        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Score >= result[i].Score,
                $"Result {i - 1} score ({result[i - 1].Score}) should be >= result {i} score ({result[i].Score})");
        }

        // "Hotel California" should rank highest: keywordCoverage=1, titleCoverage=1 -> 100 + 5 = 105
        Assert.Equal("Hotel California", result[0].Item.Name);
    }

    // ─── Score: Empty Inputs ────────────────────────────────────────────

    [Fact]
    public void Score_EmptyKeywordTokens_ReturnsEmptyList()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Song", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.Score(songs, Array.Empty<string>(), "en-US");

        Assert.Empty(result);
    }

    [Fact]
    public void Score_EmptySongList_ReturnsEmptyList()
    {
        var keywords = KeywordMatcher.Tokenize("hello", "en-US");

        var result = KeywordMatcher.Score(new List<BaseItem>(), keywords, "en-US");

        Assert.Empty(result);
    }

    // ─── Score: Multi-Locale Stop Words ─────────────────────────────────

    [Fact]
    public void Score_ItalianStopWordsRemovedFromTitle()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Il Sole", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // "il sole" tokenizes to ["sole"] with it-IT
        var keywords = KeywordMatcher.Tokenize("sole", "it-IT");

        var result = KeywordMatcher.Score(songs, keywords, "it-IT");

        Assert.Single(result);
        Assert.Equal(105.0, result[0].Score); // both coverages 1.0 + positional bonus
    }

    // ─── Score: Edge Cases ──────────────────────────────────────────────

    [Fact]
    public void Score_SongWithNullName_Skipped()
    {
        var songs = new List<Audio>
        {
            new() { Name = null!, Id = Guid.NewGuid() },
            new() { Name = "Hello", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("hello", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Single(result);
        Assert.Equal("Hello", result[0].Item.Name);
    }

    [Fact]
    public void Score_NoMatches_ReturnsEmptyList()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("stairway heaven", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Empty(result);
    }

    [Fact]
    public void Score_MultipleSongsSameScore_BothIncluded()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Song Alpha", Id = Guid.NewGuid() },
            new() { Name = "Alpha Song", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = KeywordMatcher.Tokenize("alpha song", "en-US");

        var result = KeywordMatcher.Score(songs, keywords, "en-US");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Tokenize_SingleNonStopWord_ReturnsSingleToken()
    {
        var result = KeywordMatcher.Tokenize("hello", "en-US");
        Assert.Equal(new[] { "hello" }, result);
    }

    [Fact]
    public void Tokenize_PunctuationOnlyInput_ReturnsEmptyArray()
    {
        var result = KeywordMatcher.Tokenize("!@#$%^&*()", "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void Score_DuplicateKeywordsInInput_Harmless()
    {
        // User says "hello hello" — tokenized to ["hello", "hello"]
        // Title "Hello" -> tokens ["hello"]
        // keywordCoverage: 2/2 keywords found? "hello" is in title tokens -> found for both = 2/2 = 1.0
        // titleCoverage: 1/1 title token covered = 1.0
        var songs = new List<Audio>
        {
            new() { Name = "Hello", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.Score(songs, new[] { "hello", "hello" }, "en-US");

        Assert.Single(result);
        Assert.Equal(105.0, result[0].Score);
    }
}
