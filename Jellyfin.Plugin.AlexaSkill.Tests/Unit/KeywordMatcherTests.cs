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
        // JF-389 added ja/nl/hi/ar sets, so the unknown-locale case uses a locale with
        // no set (ko-KR): all words preserved except English stop words (JF-384).
        var result = KeywordMatcher.Tokenize("watashi no uta", "ko-KR");
        Assert.Equal(new[] { "watashi", "no", "uta" }, result);
    }

    [Fact]
    public void Tokenize_NlNL_StripsDutchStopWords()
    {
        // JF-389: Dutch function words must not pollute keyword coverage.
        var result = KeywordMatcher.Tokenize("speel het lied van de band", "nl-NL");
        Assert.Equal(new[] { "speel", "lied", "band" }, result);
    }

    [Fact]
    public void Tokenize_JaJP_StripsJapaneseParticles_KeepsNo()
    {
        // JF-389: romaji particles are stripped under ja-JP; "no" stays by design
        // (ambiguity with the English/Italian word, same rationale as JF-383).
        var result = KeywordMatcher.Tokenize("watashi no uta wo kike", "ja-JP");
        Assert.Equal(new[] { "watashi", "no", "uta", "kike" }, result);
    }

    [Fact]
    public void Tokenize_HiIN_StripsHindiPostpositions()
    {
        // JF-389: Hindi postpositions and conjunctions are stripped under hi-IN.
        var result = KeywordMatcher.Tokenize("queen ka gaana chalao", "hi-IN");
        Assert.Equal(new[] { "queen", "gaana", "chalao" }, result);
    }

    [Fact]
    public void Tokenize_ArSA_StripsArabicPrepositions()
    {
        // JF-389: Arabic prepositions are stripped under ar-SA.
        var result = KeywordMatcher.Tokenize("aghani queen fi al library", "ar-SA");
        Assert.Equal(new[] { "aghani", "queen", "al", "library" }, result);
    }

    [Fact]
    public void Tokenize_EmptyLocale_EnglishStopWordsStillRemoved()
    {
        // Contract changed by JF-384: English stop words are stripped under EVERY locale
        // (including empty/unknown), because English titles spoken under non-English
        // locales carry English function words that would otherwise veto the keyword
        // coverage. Locale-specific stop words are NOT applied when the locale is unknown.
        var result = KeywordMatcher.Tokenize("the a song", string.Empty);
        Assert.Equal(new[] { "song" }, result);
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
        // User says "hello hello" - tokenized to ["hello", "hello"]
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

    // JF-383: abbreviation canonicalization (street/st, road/rd, avenue/ave,
    // part/pt, volume/vol). Music taggers abbreviate title words ("Decatur St.");
    // the spoken full word ("street") must match the abbreviated tagged token,
    // bidirectionally. The map intentionally EXCLUDES number/no: "no" is a real
    // word in English/Italian and a grammatical particle in Japanese
    // ("watashi no uta"), so canonicalizing it globally would corrupt token
    // streams (see the ja-JP regression guard below).

    [Fact]
    public void Tokenize_CanonicalizesAbbreviatedTitleTokens()
    {
        // The live JF-383 repro: tagged title "Decatur St." must tokenize to the same
        // tokens as the spoken "Decatur Street" so the n-gram/keyword search matches.
        var abbreviated = KeywordMatcher.Tokenize("Decatur St.", "en-US");
        var spoken = KeywordMatcher.Tokenize("Decatur Street", "en-US");

        Assert.Equal(new[] { "decatur", "street" }, abbreviated);
        Assert.Equal(abbreviated, spoken);
    }

    [Fact]
    public void Tokenize_CanonicalizesAbbreviations_Bidirectional()
    {
        // Reverse direction: the abbreviated keyword must map to the same canonical
        // token as the spelled-out title word (and the other map members likewise).
        Assert.Equal(KeywordMatcher.Tokenize("Street", "en-US"), KeywordMatcher.Tokenize("St.", "en-US"));
        Assert.Equal(KeywordMatcher.Tokenize("Road", "en-US"), KeywordMatcher.Tokenize("Rd.", "en-US"));
        Assert.Equal(KeywordMatcher.Tokenize("Avenue", "en-US"), KeywordMatcher.Tokenize("Ave.", "en-US"));
        Assert.Equal(KeywordMatcher.Tokenize("Part", "en-US"), KeywordMatcher.Tokenize("Pt.", "en-US"));
        Assert.Equal(KeywordMatcher.Tokenize("Volume", "en-US"), KeywordMatcher.Tokenize("Vol.", "en-US"));
    }

    [Fact]
    public void Score_SpokenKeywordMatchesAbbreviatedTitle()
    {
        // Integration shape of the live repro: song titled "Decatur St.", user keyword "street".
        var songs = new List<Audio>
        {
            new() { Name = "Decatur St.", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.Score(songs, KeywordMatcher.Tokenize("street", "en-US"), "en-US");

        Assert.Single(result);
    }

    [Fact]
    public void Tokenize_SaintSharesStClass()
    {
        // Tagged "St." is ambiguous between Street and Saint ("Decatur St." vs
        // "St. Louis Blues"), so "saint" joins the st equivalence class: a spoken
        // "saint louis" must find a tagged "St. Louis Blues" (code-review JF-383).
        var songs = new List<Audio>
        {
            new() { Name = "St. Louis Blues", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.Score(songs, KeywordMatcher.Tokenize("saint louis", "en-US"), "en-US");

        Assert.Single(result);
    }

    [Fact]
    public void Tokenize_JapaneseParticleNo_IsNotCanonicalized()
    {
        // Hard guard: "no" (the Japanese particle) must pass through unchanged, which
        // is why number/no is excluded from the abbreviation map AND from the ja stop
        // word set (JF-389): it is a real word in English/Italian, and stripping it
        // under ja-JP would corrupt English titles ("No Surprises").
        var result = KeywordMatcher.Tokenize("watashi no uta", "ja-JP");
        Assert.Equal(new[] { "watashi", "no", "uta" }, result);
    }

    // JF-388: when candidates tie on phonetic coverage, the residual (non-matching)
    // keywords break the tie. Live case: query 'the cater street' -> [cater, street];
    // 'Decatur St.' -> [decatur, street] (cater PartialRatio decatur = 80);
    // 'St. Gregory' -> [street, gregory] (cater PartialRatio gregory = 20).
    // Both pass the 50% phonetic gate via 'street', but Decatur St. must rank FIRST.
    // Without the tiebreak, St. Gregory won 42.5 vs 37.5 via the PositionalBonus
    // misfire on the canonicalized 'St.' in first position.
    [Fact]
    public void ScorePhonetic_ResidualTiebreak_RightCandidateRanksFirst()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Decatur St.", Id = Guid.NewGuid() },
            new() { Name = "St. Gregory", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();
        var keywordTokens = KeywordMatcher.Tokenize("the cater street", "en-US");

        var phonetic = KeywordMatcher.ScorePhonetic(songs, keywordTokens, "en-US");

        Assert.NotEmpty(phonetic);
        Assert.Equal("Decatur St.", phonetic[0].Item.Name);
    }

    // JF-388 garbage control: the residual tiebreak must not let garbage keywords
    // gain ranking. 'xyzzyfoo street' vs 'Decatur St.': xyzzyfoo matches nothing
    // (PartialRatio vs decatur ~ 0), so the residual contribution is ~0 and this
    // candidate must NOT outrank anything on the residual signal alone.
    [Fact]
    public void ScorePhonetic_ResidualTiebreak_GarbageKeyword_DoesNotGainRanking()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Decatur St.", Id = Guid.NewGuid() },
            new() { Name = "St. Gregory", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();
        var keywordTokens = KeywordMatcher.Tokenize("xyzzyfoo street", "en-US");

        var phonetic = KeywordMatcher.ScorePhonetic(songs, keywordTokens, "en-US");

        // Both pass the 50% gate via 'street'; the residual for xyzzyfoo is ~0 for
        // both, so the tiebreak must NOT create a decisive gap (scores stay close).
        if (phonetic.Count == 2)
        {
            Assert.True(Math.Abs(phonetic[0].Score - phonetic[1].Score) < 10,
                $"garbage residual must not create a decisive gap: {phonetic[0].Item.Name}={phonetic[0].Score} vs {phonetic[1].Item.Name}={phonetic[1].Score}");
        }
    }

    // JF-388 saint-class guard: 'saint' must still find 'St. Gregory' via the exact
    // matcher (both canonicalize to 'street'). The residual tiebreak only applies
    // in ScorePhonetic, not in Score, so the exact path is unchanged.
    [Fact]
    public void Score_SaintQuery_StGregory_StillExactMatches()
    {
        var songs = new List<Audio>
        {
            new() { Name = "St. Gregory", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.Score(songs, KeywordMatcher.Tokenize("saint gregory", "en-US"), "en-US");

        Assert.NotEmpty(result);
        Assert.Equal("St. Gregory", result[0].Item.Name);
    }

    [Fact]
    public void Tokenize_NonAbbreviationTokens_Unchanged()
    {
        // Tokens that merely share letters with abbreviations must not be touched.
        var result = KeywordMatcher.Tokenize("stone roadster storage", "en-US");
        Assert.Equal(new[] { "stone", "roadster", "storage" }, result);
    }

    // JF-384 live follow-up: an English title spoken under a NON-English locale carries
    // English function words that the locale's stop-word list does not strip
    // (it-IT keeps "the" -> [the, cater, street], phonetic coverage 1/3 = 33% < 50%).
    // This also asymmetrizes index vs query: the n-gram index is built with "en-US"
    // (strips "the" from titles), the query with the user locale. Fix: always strip the
    // ENGLISH stop-word set in addition to the locale set (music titles are mostly
    // English; its function words are never meaningful match keywords).
    [Fact]
    public void Tokenize_CrossLocale_StripsEnglishStopWords()
    {
        // The exact live repro: it-IT request, English title words.
        Assert.Equal(new[] { "cater", "street" }, KeywordMatcher.Tokenize("the cater street", "it-IT"));
        Assert.Equal(new[] { "cater", "street" }, KeywordMatcher.Tokenize("the cater street", "en-US"));

        // Other locales too, and locale stop words still stripped alongside.
        Assert.Equal(new[] { "cater", "street" }, KeywordMatcher.Tokenize("the cater and street", "de-DE"));
        Assert.Equal(new[] { "sole", "luna" }, KeywordMatcher.Tokenize("il the sole e la luna", "it-IT"));
    }

    // JF-384 AC#1: quantify the two matchers on the live accent-drift repro.
    // Spoken "Decature Street" arrives ASR-mangled as "the cater street"; the library
    // title is "Decatur St.". Post tokenization + canonicalization:
    //   query -> [cater, street], title -> [decatur, street].
    // DM(cater)=KTR vs DM(decatur)=TKTR: no phonetic collision on the drifted word,
    // but "street" matches exactly. So Score (100% keyword coverage) must MISS, while
    // ScorePhonetic (>=50% coverage, 0.75 penalty) must FIND it at exactly 50%.
    [Fact]
    public void JF384_Diagnostics_ScoreMisses_ScorePhoneticFinds()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Decatur St.", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();
        var keywordTokens = KeywordMatcher.Tokenize("the cater street", "en-US");

        Assert.Equal(new[] { "cater", "street" }, keywordTokens);
        Assert.Equal(new[] { "decatur", "street" }, KeywordMatcher.Tokenize("Decatur St.", "en-US"));

        var exact = KeywordMatcher.Score(songs, keywordTokens, "en-US");
        Assert.Empty(exact); // the full-word veto: one drifted word kills the match

        var phonetic = KeywordMatcher.ScorePhonetic(songs, keywordTokens, "en-US");
        Assert.NotEmpty(phonetic); // 1/2 coverage = 50%, at the gate
    }

    // JF-384 garbage control: a single-word query that matches nothing (0% coverage)
    // must miss on BOTH matchers. This is the honest control: a two-word query with one
    // real word legitimately passes the 50% phonetic gate (same semantics as the global
    // n-gram path today), but one garbage word alone has nothing to stand on.
    [Fact]
    public void JF384_Diagnostics_SingleGarbageWord_MissesBothMatchers()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Decatur St.", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();
        var keywordTokens = KeywordMatcher.Tokenize("xyzzyfoo", "en-US");

        Assert.Empty(KeywordMatcher.Score(songs, keywordTokens, "en-US"));
        Assert.Empty(KeywordMatcher.ScorePhonetic(songs, keywordTokens, "en-US"));
    }
}
