using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-646: pins the catalog-side Latin->kana generator's deterministic decisions
/// (vowel-team long vowels, geminate gating, moraic nasals, the article, the
/// ambiguous-shape alternates), the ROUND-TRIP contract with the query-side
/// KatakanaRomanizer (one 'ー' per long vowel so 'Queen' -> 'クイーン' -> 'kuin',
/// never 'クイーーン'), the ja-only dispatch wiring with the per-name cap, and the
/// upload-pipeline integration (CatalogPayload carries the kana synonyms beside
/// the Latin value, truncated to the 140-char slot limit).
/// </summary>
public class KatakanaSynonymGeneratorTests
{
    // --- generator table ---

    [Theory]
    [InlineData("Queen", "クイーン|クィーン")]                       // qu + long ee + moraic n; the modern small-kana alternate
    [InlineData("Keane", "キーン|ケン")]                             // silent-e lengthening; the /ɛ/ 'ea' alternate
    [InlineData("The Beatles", "ザ・ビートルズ|ビートルズ|ザ・ベトルズ|ザ ビートルズ")] // article, article-drop, ea-alt, space-join
    [InlineData("The Smiths", "ザ・スミス|スミス|ザ スミス")]        // th voiceless before s, final s merged into the sibilant
    [InlineData("Pink Floyd", "ピンク・フロイド|ピンク フロイド")]  // moraic n before k; fl cluster; no geminate after the /ɔɪ/ diphthong
    [InlineData("Bob", "ボブ")]                                     // word-final b takes the plain epenthetic u (no geminate)
    [InlineData("D'Angelo", "ダンジェロ")]                          // punctuation stripped; soft g before e
    [InlineData("Rock", "ロック")]                                  // ck geminate
    [InlineData("Led Zeppelin", "レッド・ゼッペリン|レッド ゼッペリン")] // final d geminated after a short vowel; the conventional ゼッペリン shape
    [InlineData("Backstreet Boys", "バックストリート・ボイズ|バックストリート ボイズ")] // ck + st cluster + long ee
    [InlineData("Soul", "ソウル")]                                  // ou diphthong keeps two morae
    [InlineData("Myers", "マース")]                                 // no-yoon fallback folds the vowel unit once (review: never マーアース)
    [InlineData("Ryerson", "ラーソン")]                             // the Xye shape through the same fallback
    [InlineData("Whitney", "ウィトニー")]                           // final 'ey' is /iː/ (review: never ウィトネユ)
    [InlineData("Buckley", "ブックリー")]                           // final 'ey' after the ck geminate
    public void Generate_RepresentativeNames_ExactTable(string name, string expectedPipeJoined)
    {
        var expected = expectedPipeJoined.Split('|');
        var actual = KatakanaSynonymGenerator.Generate(name);
        Assert.True(
            expected.SequenceEqual(actual),
            $"name={name} expected=[{string.Join("|", expected)}] actual=[{string.Join("|", actual)}]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Generate_ShortOrNullNames_ReturnEmpty(string? name)
    {
        Assert.Empty(KatakanaSynonymGenerator.Generate(name!));
    }

    [Fact]
    public void Generate_AlreadyKanaName_ReturnsEmpty()
    {
        // The name itself is the naturalized spoken form ER matches; a kana-of-kana
        // pass would only duplicate it.
        Assert.Empty(KatakanaSynonymGenerator.Generate("クイーン"));
        Assert.Empty(KatakanaSynonymGenerator.Generate("Queen クイーン"));
    }

    // --- review round: reverse-syllabary determinism + kana-input passthrough ---

    [Fact]
    public void TryKatakana_CollidingValues_ResolveToTheModernKanaDeterministically()
    {
        // The tie-break is explicit by construction (no same-priority pairs), so
        // dictionary enumeration order can never decide: ジ over the historical
        // ヂ, ズ over ヅ, and the modern ジャ digraph over the ヂャ shape.
        Assert.True(KatakanaRomanizer.TryKatakana("ji", out string? ji));
        Assert.Equal("ジ", ji);
        Assert.True(KatakanaRomanizer.TryKatakana("zu", out string? zu));
        Assert.Equal("ズ", zu);
        Assert.True(KatakanaRomanizer.TryKatakana("ja", out string? ja));
        Assert.Equal("ジャ", ja);
    }

    [Fact]
    public void GenerateSynonyms_KanaOrMixedNames_ProduceNoSynonyms()
    {
        // A kana name IS the naturalized spoken form; the romaji arm's Latin
        // tail-rules (AppendFinalVowel) used to graft a Latin 'u' onto the kana
        // final ('クイーンu'). Both arms now skip kana-containing names.
        Assert.Empty(JapanesePhoneticSynonyms.Generate("クイーン"));
        Assert.Empty(PhoneticSynonymGenerator.GenerateSynonyms("クイーン", "ja-JP"));
        Assert.Empty(PhoneticSynonymGenerator.GenerateSynonyms("Queen クイーン", "ja-JP"));
    }

    [Fact]
    public void Generate_ArticleOnlyName_ReturnsEmpty()
    {
        Assert.Empty(KatakanaSynonymGenerator.Generate("The"));
        Assert.Empty(KatakanaSynonymGenerator.Generate("The   "));
    }

    // --- round-trip contract with the query-side romanizer ---

    [Theory]
    [InlineData("Queen", "クイーン", "kuin")]
    [InlineData("Queen", "クィーン", "kuin")]      // the small-kana alternate folds to the same romaji
    [InlineData("Keane", "キーン", "kin")]
    [InlineData("Rock", "ロック", "rokku")]
    [InlineData("The Beatles", "ザ・ビートルズ", "za・bitoruzu")]
    public void Romanize_OfGeneratedKana_ExactRoundTrips(string name, string kana, string romaji)
    {
        var variants = KatakanaSynonymGenerator.Generate(name);
        Assert.Contains(kana, variants);
        Assert.Equal(romaji, KatakanaRomanizer.Romanize(kana));
    }

    [Fact]
    public void Generate_QueenKana_DoubleMetaphoneCodeEqualsQueen()
    {
        // The catalog-side mirror of the JF-643 load-bearing property: the
        // generated kana romanizes onto the same Double Metaphone codes as the
        // Latin library name, so query-side and catalog-side agree on the bridge.
        var kanaCodes = DoubleMetaphone.Encode(KatakanaRomanizer.Romanize(KatakanaSynonymGenerator.Generate("Queen")[0]));
        var nameCodes = DoubleMetaphone.Encode("Queen");

        Assert.True(
            FuzzyMatcher.PhoneticCodesMatch(kanaCodes.Primary, kanaCodes.Alternate, nameCodes.Primary, nameCodes.Alternate),
            $"codes differ: kana=({kanaCodes.Primary},{kanaCodes.Alternate}) name=({nameCodes.Primary},{nameCodes.Alternate})");
    }

    public static TheoryData<string> CoverageBattery => new()
    {
        "Queen",
        "Keane",
        "The Beatles",
        "The Smiths",
        "Pink Floyd",
        "Led Zeppelin",
        "Backstreet Boys",
        "Soul Coughing",
        "Nirvana",
        "Bee Gees",
        "Weather Report",
        "Max",
        "City",
        "D'Angelo",
        "AC/DC",
        "P!nk",
        "Kiss",
        "Jazz",
        "Simon & Garfunkel",
        "The Backstreet Boys",
        "Myers",
        "Ryerson",
        "Sofya",
        "Whitney",
        "Buckley",
        "Carey"
    };

    [Theory]
    [MemberData(nameof(CoverageBattery))]
    public void Generate_EveryVariant_IsWellFormedKana(string name)
    {
        // Structural guarantees for arbitrary library names: kana-only output
        // (no Latin leaks into a kana synonym), exactly ONE long-vowel mark per
        // lengthening (never 'ーー'), and no dangling sokuon.
        foreach (string variant in KatakanaSynonymGenerator.Generate(name))
        {
            Assert.True(KatakanaRomanizer.ContainsKana(variant), $"{name}: no kana in '{variant}'");
            Assert.All(variant, c => Assert.False(char.IsAsciiLetter(c), $"{name}: ASCII letter '{c}' in '{variant}'"));
            Assert.DoesNotContain("ーー", variant, StringComparison.Ordinal);
            Assert.DoesNotContain("ッー", variant, StringComparison.Ordinal);
            Assert.False(variant.EndsWith('ッ'), $"{name}: dangling sokuon in '{variant}'");
        }
    }

    [Theory]
    [MemberData(nameof(CoverageBattery))]
    public void Generate_RoundTripsThroughRomanizer_WithoutAsciiArtifacts(string name)
    {
        // The generated kana must survive the romanizer (the query-side path a
        // user's spoken form takes) as a kana-only -> romaji-only string. The
        // katakana middle dot passes through unchanged by design (it is the word
        // separator, not a syllable), so it is excluded from the kana check.
        foreach (string variant in KatakanaSynonymGenerator.Generate(name))
        {
            string roundTripped = KatakanaRomanizer.Romanize(variant);
            Assert.DoesNotContain("ー", roundTripped, StringComparison.Ordinal);
            Assert.All(
                roundTripped.Where(c => c != '・'),
                c => Assert.False(KatakanaRomanizer.ContainsKana(c.ToString()), $"{name}: kana survived romanization in '{roundTripped}'"));
        }
    }

    // --- dispatch wiring: ja-only, capped, kana first ---

    [Fact]
    public void GenerateSynonyms_JaDispatch_KanaFirstThenRomaji_CappedAtFive()
    {
        // The ja arm combines the kana renderings with the romaji approximations;
        // the cap is the Romance generators' 5 and the kana (device-captured)
        // forms come first so they survive it.
        var ja = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles", "ja-JP");
        Assert.Equal(
            new[] { "ザ・ビートルズ", "ビートルズ", "ザ・ベトルズ", "ザ ビートルズ", "Beatres" },
            ja);

        var queen = PhoneticSynonymGenerator.GenerateSynonyms("Queen", "ja-JP");
        Assert.Equal(new[] { "クイーン", "クィーン" }, queen);
    }

    [Theory]
    [MemberData(nameof(CoverageBattery))]
    public void GenerateSynonyms_JaDispatch_RespectsPerNameCap(string name)
    {
        var ja = PhoneticSynonymGenerator.GenerateSynonyms(name, "ja-JP");
        Assert.True(ja.Count <= 5, $"{name}: {ja.Count} synonyms exceed the cap");
        if (ja.Count > 0)
        {
            Assert.True(KatakanaRomanizer.ContainsKana(ja[0]), $"{name}: first synonym '{ja[0]}' is not kana");
        }
    }

    [Theory]
    [MemberData(nameof(TestLocales.LocaleRows), MemberType = typeof(TestLocales))]
    public void GenerateSynonyms_NonJaLocales_EmitNoKana(string locale)
    {
        // The wiring is the ja arm of the dispatch only: every other locale's
        // synonym stream stays Latin-only (byte-identical payload behavior).
        if (locale == "ja-JP")
        {
            return;
        }

        foreach (string name in new[] { "The Beatles", "Queen", "Soul Coughing" })
        {
            foreach (string synonym in PhoneticSynonymGenerator.GenerateSynonyms(name, locale))
            {
                Assert.False(
                    KatakanaRomanizer.ContainsKana(synonym),
                    $"{locale}: kana leaked into '{synonym}' for '{name}'");
            }
        }
    }

    // --- upload-pipeline integration ---

    [Fact]
    public void CatalogPayload_JaLocale_CarriesKanaSynonymsBesideLatinValue()
    {
        var items = new[] { (Guid.NewGuid(), "Queen") };
        var payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, PhoneticSynonymGenerator.GenerateSynonyms, "ja-JP");

        var value = Assert.Single(payload.Values);
        Assert.Equal("Queen", value.Name.Value);
        Assert.NotNull(value.Name.Synonyms);
        Assert.Contains("クイーン", value.Name.Synonyms);
        Assert.Contains("クィーン", value.Name.Synonyms);
    }

    [Fact]
    public void CatalogPayload_NonJaLocale_KeepsSynonymsKanaFree()
    {
        var items = new[] { (Guid.NewGuid(), "The Beatles") };
        var payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, PhoneticSynonymGenerator.GenerateSynonyms, "it-IT");

        var value = Assert.Single(payload.Values);
        Assert.NotNull(value.Name.Synonyms);
        Assert.All(value.Name.Synonyms, s => Assert.False(KatakanaRomanizer.ContainsKana(s)));
    }

    [Fact]
    public void CatalogPayload_JaLocale_LongNameSynonymsTruncatedToSlotLimit()
    {
        string longName = "Queen " + new string('a', 200);
        var items = new[] { (Guid.NewGuid(), longName) };
        var payload = CatalogPayload.FromItems(
            CatalogType.Artist, items, PhoneticSynonymGenerator.GenerateSynonyms, "ja-JP");

        var value = Assert.Single(payload.Values);
        Assert.All(value.Name.Synonyms!, s => Assert.True(s.Length <= SlotValueHelper.MaxSlotValueLength, $"synonym too long: {s.Length}"));
    }
}
