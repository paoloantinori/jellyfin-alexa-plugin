using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-643: pins the katakana-to-romaji transliterator's deterministic decisions
/// (long-vowel contraction, sokuon gemination, moraic n, digraphs, hiragana
/// folding, non-kana passthrough) and the LOAD-BEARING phonetic property: the
/// romanized query's Double Metaphone code must equal the library name's, so the
/// phonetic layer bridges a katakana query exactly like the documented
/// ASR-accent case ('kuin' vs 'Queen').
/// </summary>
public class KatakanaRomanizerTests
{
    [Theory]
    [InlineData("クイーン", "kuin")]        // long-vowel contraction: ー dropped
    [InlineData("ジャズ", "jazu")]          // ジャ digraph + ズ
    [InlineData("ロック", "rokku")]         // sokuon doubles the next consonant
    [InlineData("ホーン", "hon")]           // contraction at word end
    [InlineData("コーヒー", "kohi")]        // two long marks, both contracted
    [InlineData("パン", "pan")]             // moraic n + handakuten
    [InlineData("チョコレート", "chokoreto")] // チョ digraph + contraction
    [InlineData("マッチ", "matchi")]        // sokuon before 'ch' doubles as 't' (Hepburn)
    [InlineData("シュガー", "shuga")]       // シュ digraph
    [InlineData("ヴァイオリン", "vaiorin")] // ヴ-family digraph
    [InlineData("ウィルコ", "wiruko")]      // ウィ digraph (loanword 'Wilco')
    [InlineData("ボヘミアン・ラプソディ", "bohemian・rapusodi")] // middle dot passes through unchanged
    [InlineData("くいーん", "kuin")]        // hiragana maps with the same values
    [InlineData("にほん", "nihon")]         // hiragana base syllabary
    [InlineData("ザ・ビートルズ", "za・bitoruzu")]
    public void Romanize_DeterministicTable(string input, string expected)
    {
        Assert.Equal(expected, KatakanaRomanizer.Romanize(input));
    }

    [Fact]
    public void Romanize_MixedScript_PassesNonKanaThrough()
    {
        // Latin keeps its spacing and case; kanji passes through unchanged (no
        // dictionary, accepted and documented).
        Assert.Equal("the kuin", KatakanaRomanizer.Romanize("the クイーン"));
        Assert.Equal("音楽kuin", KatakanaRomanizer.Romanize("音楽クイーン"));
        Assert.Equal("kuin 2025!", KatakanaRomanizer.Romanize("クイーン 2025!"));
    }

    [Fact]
    public void Romanize_NoKana_ReturnsSameInstance()
    {
        // The fast no-op path: Latin-only queries pay one range scan and no
        // allocation (the same-instance contract the hot paths rely on).
        foreach (string latin in new[] { string.Empty, "queen", "Bohemian Rhapsody", "123 & test", "  " })
        {
            Assert.Same(latin, KatakanaRomanizer.Romanize(latin));
        }
    }

    [Fact]
    public void Romanize_DanglingSokuon_Dropped()
    {
        // A sokuon at the end of the input (or before a vowel/non-kana char) is
        // not a real geminate shape and is dropped.
        Assert.Equal("ro", KatakanaRomanizer.Romanize("ロッ"));
        Assert.Equal("roa", KatakanaRomanizer.Romanize("ロッア"));
    }

    [Fact]
    public void Romanize_Kuin_DoubleMetaphoneCodeEqualsQueen()
    {
        // THE load-bearing property (JF-643): after romanization the query must
        // reach the phonetic layer that already bridges ASR accent drift. Verified
        // with the production helpers (DoubleMetaphone + FuzzyMatcher.PhoneticCodesMatch),
        // not a reimplementation.
        var queryCodes = DoubleMetaphone.Encode(KatakanaRomanizer.Romanize("クイーン"));
        var nameCodes = DoubleMetaphone.Encode("Queen");

        Assert.True(
            FuzzyMatcher.PhoneticCodesMatch(queryCodes.Primary, queryCodes.Alternate, nameCodes.Primary, nameCodes.Alternate),
            $"codes differ: query=({queryCodes.Primary},{queryCodes.Alternate}) name=({nameCodes.Primary},{nameCodes.Alternate})");
    }

    [Fact]
    public void Romanize_JazuRokku_DoubleMetaphoneCodesEqualJazzRock()
    {
        // The genre-resolution tier (PlayByGenre) and the artist path rely on the
        // same bridge for the two other live-verified katakana probes.
        var jazu = DoubleMetaphone.Encode(KatakanaRomanizer.Romanize("ジャズ"));
        var jazz = DoubleMetaphone.Encode("Jazz");
        Assert.True(FuzzyMatcher.PhoneticCodesMatch(jazu.Primary, jazu.Alternate, jazz.Primary, jazz.Alternate));

        var rokku = DoubleMetaphone.Encode(KatakanaRomanizer.Romanize("ロック"));
        var rock = DoubleMetaphone.Encode("Rock");
        Assert.True(FuzzyMatcher.PhoneticCodesMatch(rokku.Primary, rokku.Alternate, rock.Primary, rock.Alternate));
    }

    [Fact]
    public void FuzzyMatcher_PhoneticOverload_KatakanaQueryFindsLatinName()
    {
        // The JF-643 wiring inside FuzzyMatcher: the query is romanized at the
        // entry, so the phonetic overload (the artist path's tier-4 matcher)
        // resolves 'クイーン' to 'Queen' through the pre-computed index codes,
        // exactly as the Latin ASR-drift query 'kuin' does.
        var queen = new TestCandidate(Guid.NewGuid(), "Queen");
        var decoy = new TestCandidate(Guid.NewGuid(), "Porcupine Tree");
        var candidates = new[] { queen, decoy };
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [queen.Id] = DoubleMetaphone.Encode(queen.Name),
            [decoy.Id] = DoubleMetaphone.Encode(decoy.Name)
        };

        TestCandidate? match = FuzzyMatcher.FindBestMatch(
            "クイーン",
            candidates,
            c => c.Name,
            c => c.Id,
            id => codes.TryGetValue(id, out var code) ? code : null,
            FuzzyMatcher.DefaultThreshold);

        Assert.NotNull(match);
        Assert.Equal("Queen", match.Name);
    }

    [Fact]
    public void FuzzyMatcher_PlainOverload_KatakanaQueryBelowThreshold_NoMatch()
    {
        // Documents the layer split: Levenshtein alone scores 'kuin' vs 'Queen' at
        // 25 (below the 60 threshold), so the plain overload returns no match for a
        // katakana query even after romanization. The Double Metaphone layer is the
        // bridge; production artist paths use the phonetic overload.
        var queen = new TestCandidate(Guid.NewGuid(), "Queen");

        TestCandidate? match = FuzzyMatcher.FindBestMatch(
            "クイーン",
            new[] { queen },
            c => c.Name,
            FuzzyMatcher.DefaultThreshold);

        Assert.Null(match);
    }

    internal sealed record TestCandidate(Guid Id, string Name);
}
