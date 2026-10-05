using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Query-side kana-to-romaji transliterator (JF-643). Japanese ASR transcribes
/// foreign names as katakana ('クイーン' for Queen), while the fuzzy/phonetic
/// layers (Levenshtein + Double Metaphone) are Latin-script algorithms, so a
/// kana query scores near zero against a Latin library name on every search
/// path. Romanizing the QUERY before scoring puts it on the same footing as the
/// documented ASR-accent case ('kuin' vs 'Queen', which the Double Metaphone
/// layer already bridges). Library-side values are never transliterated: the
/// script gap is query-side, and the artist index's pre-computed phonetic codes
/// stay built from the Latin names.
/// Deterministic decisions, pinned by KatakanaRomanizerTests:
/// - long-vowel mark (ー): contraction, the mark is dropped. The preceding
///   vowel already carries the length, and doubling it ('クイーン' as 'kuiin')
///   pushes the query one letter further from the library name inside the
///   length band the phonetic floor uses. 'クイーン' romanizes to 'kuin', whose
///   Double Metaphone code equals 'Queen''s (the load-bearing property).
/// - sokuon (small っ/ッ): doubles the next syllable's consonant ('ロック' to
///   'rokku'); before a 'ch' syllable the doubled consonant is 't' ('マッチ'
///   to 'matchi', Hepburn).
/// - moraic nasal (ん/ン): always 'n', never the Hepburn n' variant.
/// - hiragana maps with the same syllable values; kanji and every other
///   non-kana character pass through unchanged (no dictionary: a kanji run
///   cannot be romanized here, accepted and documented).
/// KNOWN NARROWING (JF-643 review, CLOSED on the in-memory paths by JF-755):
/// normalization was originally asymmetric (query only, library values never),
/// so a kana query against a KATAKANA-TAGGED library name, which previously
/// exact-matched on the Contains tier, missed on every tier ('クイーン'
/// romanizes to 'kuin', which cannot equal the kana name, and Double
/// Metaphone encodes kana to nothing, so the phonetic floor could not rescue
/// it). JF-755 closes it symmetrically at INDEX-BUILD time: the artist index
/// carries each kana-containing name's romaji alongside the original (the
/// matching legs bridge through it, ArtistSearch.QueryNameFor), the song
/// n-gram index unions the romanized token stream into its keys, and the
/// kana-named artist's phonetic codes are computed from the romaji. Library
/// values are STILL never transliterated in place (the parallel-key shape).
/// Residual (accepted, the JF-381/JF-417 cold-window divergence class): the
/// database fallback tiers search Jellyfin's own index, which has no
/// romanizer, so a kana query during the cold window still honestly
/// not-finds a kana-tagged name. ALBUM surfaces sit in that narrowing
/// PERMANENTLY, not just cold: the album query is romanized (AlbumPlayService,
/// PlayAlbumIntentHandler) but albums have no in-memory index to carry a
/// candidate-side romaji key, and the album candidate legs score raw names
/// (tracked as JF-773; the cheap shape when picked up is the score-time
/// KeywordMatcher.TitleTokens union, not a new index).
/// </summary>
internal static class KatakanaRomanizer
{
    private const char LongVowelMark = 'ー';
    private const char SmallTsu = 'ッ';

    /// <summary>
    /// Base syllabary keyed on the KATAKANA code point; hiragana input is folded
    /// to the katakana key first (<see cref="ToKatakanaKey"/>), so one table
    /// serves both scripts (same syllable values by design).
    /// </summary>
    private static readonly Dictionary<char, string> Syllables = new()
    {
        ['ァ'] = "a", ['ア'] = "a", ['ィ'] = "i", ['イ'] = "i",
        ['ゥ'] = "u", ['ウ'] = "u", ['ェ'] = "e", ['エ'] = "e",
        ['ォ'] = "o", ['オ'] = "o", ['ヴ'] = "vu",
        ['カ'] = "ka", ['キ'] = "ki", ['ク'] = "ku", ['ケ'] = "ke", ['コ'] = "ko",
        ['ガ'] = "ga", ['ギ'] = "gi", ['グ'] = "gu", ['ゲ'] = "ge", ['ゴ'] = "go",
        ['サ'] = "sa", ['シ'] = "shi", ['ス'] = "su", ['セ'] = "se", ['ソ'] = "so",
        ['ザ'] = "za", ['ジ'] = "ji", ['ズ'] = "zu", ['ゼ'] = "ze", ['ゾ'] = "zo",
        ['タ'] = "ta", ['チ'] = "chi", ['ツ'] = "tsu", ['テ'] = "te", ['ト'] = "to",
        ['ダ'] = "da", ['ヂ'] = "ji", ['ヅ'] = "zu", ['デ'] = "de", ['ド'] = "do",
        ['ナ'] = "na", ['ニ'] = "ni", ['ヌ'] = "nu", ['ネ'] = "ne", ['ノ'] = "no",
        ['ハ'] = "ha", ['ヒ'] = "hi", ['フ'] = "fu", ['ヘ'] = "he", ['ホ'] = "ho",
        ['バ'] = "ba", ['ビ'] = "bi", ['ブ'] = "bu", ['ベ'] = "be", ['ボ'] = "bo",
        ['パ'] = "pa", ['ピ'] = "pi", ['プ'] = "pu", ['ペ'] = "pe", ['ポ'] = "po",
        ['マ'] = "ma", ['ミ'] = "mi", ['ム'] = "mu", ['メ'] = "me", ['モ'] = "mo",
        ['ャ'] = "ya", ['ヤ'] = "ya", ['ュ'] = "yu", ['ユ'] = "yu", ['ョ'] = "yo", ['ヨ'] = "yo",
        ['ラ'] = "ra", ['リ'] = "ri", ['ル'] = "ru", ['レ'] = "re", ['ロ'] = "ro",
        ['ヮ'] = "wa", ['ワ'] = "wa", ['ヰ'] = "i", ['ヱ'] = "e", ['ヲ'] = "o", ['ン'] = "n"
    };

    /// <summary>
    /// Two-kana loanword combinations (yoon digraphs and the extended vowel
    /// combos modern loanwords use: ファ/ウィ/ティ/チェ...), keyed on katakana
    /// pairs and checked before the single-character table.
    /// </summary>
    private static readonly Dictionary<(char First, char Second), string> Digraphs = new()
    {
        [('キ', 'ャ')] = "kya", [('キ', 'ュ')] = "kyu", [('キ', 'ョ')] = "kyo",
        [('ギ', 'ャ')] = "gya", [('ギ', 'ュ')] = "gyu", [('ギ', 'ョ')] = "gyo",
        [('シ', 'ャ')] = "sha", [('シ', 'ュ')] = "shu", [('シ', 'ョ')] = "sho", [('シ', 'ェ')] = "she",
        [('ジ', 'ャ')] = "ja", [('ジ', 'ュ')] = "ju", [('ジ', 'ョ')] = "jo", [('ジ', 'ェ')] = "je",
        [('チ', 'ャ')] = "cha", [('チ', 'ュ')] = "chu", [('チ', 'ョ')] = "cho", [('チ', 'ェ')] = "che",
        [('ヂ', 'ャ')] = "ja", [('ヂ', 'ュ')] = "ju", [('ヂ', 'ョ')] = "jo",
        [('ニ', 'ャ')] = "nya", [('ニ', 'ュ')] = "nyu", [('ニ', 'ョ')] = "nyo",
        [('ヒ', 'ャ')] = "hya", [('ヒ', 'ュ')] = "hyu", [('ヒ', 'ョ')] = "hyo",
        [('ビ', 'ャ')] = "bya", [('ビ', 'ュ')] = "byu", [('ビ', 'ョ')] = "byo",
        [('ピ', 'ャ')] = "pya", [('ピ', 'ュ')] = "pyu", [('ピ', 'ョ')] = "pyo",
        [('ミ', 'ャ')] = "mya", [('ミ', 'ュ')] = "myu", [('ミ', 'ョ')] = "myo",
        [('リ', 'ャ')] = "rya", [('リ', 'ュ')] = "ryu", [('リ', 'ョ')] = "ryo",
        [('フ', 'ァ')] = "fa", [('フ', 'ィ')] = "fi", [('フ', 'ェ')] = "fe", [('フ', 'ォ')] = "fo", [('フ', 'ュ')] = "fyu",
        [('ヴ', 'ァ')] = "va", [('ヴ', 'ィ')] = "vi", [('ヴ', 'ェ')] = "ve", [('ヴ', 'ォ')] = "vo",
        [('ウ', 'ィ')] = "wi", [('ウ', 'ェ')] = "we", [('ウ', 'ォ')] = "wo",
        [('テ', 'ィ')] = "ti", [('テ', 'ュ')] = "tyu", [('デ', 'ィ')] = "di", [('デ', 'ュ')] = "dyu",
        [('ト', 'ゥ')] = "tu", [('ド', 'ゥ')] = "du"
    };

    /// <summary>
    /// Reverse of the two syllabary tables above (romaji key like "ka", "shi",
    /// "ti" -> canonical katakana), built from <see cref="Syllables"/> and
    /// <see cref="Digraphs"/> so the kana->romaji and romaji->kana directions
    /// share ONE syllable table and stay round-trip consistent by construction.
    /// Consumed by the catalog-side Latin->kana synonym generator (JF-646).
    /// Field order is load-bearing: static field initializers run in textual
    /// order, so this field MUST stay after the two tables it reads.
    /// Canonical choice on value collisions (several kana share one romaji
    /// value: "a" is both ア and ァ, "ji" is both ジ and ヂ): the tie-break is
    /// explicit and exhaustive, so dictionary enumeration order can never
    /// decide it. Priority 0 is the modern full-size unigram; priority 1 is
    /// every non-canonical unigram (small kana ァィゥェォ ャュョ ヮ, the obsolete
    /// ヰヱヲ, and the historical-use ヂヅ that modern loanwords never use);
    /// digraphs take 2 plus their first kana's priority, so the modern ジャ
    /// outranks the ヂャ shape. No romaji value has two candidates at the SAME
    /// priority, which is what makes the resolution deterministic by
    /// construction.
    /// </summary>
    private static readonly Dictionary<string, string> ReverseSyllables = BuildReverseSyllables();

    /// <summary>
    /// Reverse syllabary lookup for the catalog-side Latin->kana generator
    /// (JF-646): maps a romaji syllable key to its canonical katakana rendering.
    /// </summary>
    internal static bool TryKatakana(string romaji, [NotNullWhen(true)] out string? katakana)
        => ReverseSyllables.TryGetValue(romaji, out katakana);

    private static Dictionary<string, string> BuildReverseSyllables()
    {
        var canonical = new Dictionary<string, (string Kana, int Priority)>(StringComparer.Ordinal);
        foreach (var kv in Digraphs)
        {
            KeepCanonical(canonical, kv.Value, string.Concat(kv.Key.First, kv.Key.Second), priority: 2 + KanaPriority(kv.Key.First));
        }

        foreach (var kv in Syllables)
        {
            KeepCanonical(canonical, kv.Value, kv.Key.ToString(), KanaPriority(kv.Key));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in canonical)
        {
            map[kv.Key] = kv.Value.Kana;
        }

        return map;
    }

    private static void KeepCanonical(Dictionary<string, (string Kana, int Priority)> map, string romaji, string kana, int priority)
    {
        if (!map.TryGetValue(romaji, out var existing) || priority < existing.Priority)
        {
            map[romaji] = (kana, priority);
        }
    }

    private static int KanaPriority(char kana)
        => kana is 'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ' or 'ャ' or 'ュ' or 'ョ' or 'ヮ'
           || kana is 'ヰ' or 'ヱ' or 'ヲ' or 'ヂ' or 'ヅ' ? 1 : 0;

    /// <summary>
    /// The ONE candidate-side derivation gate (JF-755): whether the input carries
    /// kana AND romanizes to a usable form. False (romaji null) for null/empty
    /// input, kana-free input, and inputs whose romanization is empty/whitespace
    /// (a lone long vowel mark) or VALUE-IDENTICAL to the input: a name whose only
    /// kana-block content is romanization-inert (the katakana middle dot '・'
    /// inside 'AI・KITS' is within IsKatakana's range yet passes through
    /// unchanged) must not gain a "romaji key" equal to the name itself. True
    /// with the romanized form otherwise. Every site that derives a parallel
    /// romaji key from library text (the artist index load, the song title token
    /// union, the test fakes mirroring them) resolves through this helper so the
    /// gate rule has one owner and can never drift between production and its
    /// mirrors.
    /// </summary>
    /// <param name="input">The candidate text (library name or title).</param>
    /// <param name="romaji">The romanized form when the input has one.</param>
    /// <returns>True when the input contains kana and romanized to a non-blank, non-identical form.</returns>
    internal static bool TryRomanize(string? input, [NotNullWhen(true)] out string? romaji)
    {
        romaji = null;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        string romanized = Romanize(input);
        if (string.Equals(romanized, input, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(romanized))
        {
            return false;
        }

        romaji = romanized;
        return true;
    }

    /// <summary>
    /// Romanizes every kana run of the input, passing every other character
    /// (Latin, kanji, digits, spaces, punctuation) through unchanged. Returns
    /// the SAME instance when the input contains no kana, so Latin-only queries
    /// pay one range scan and no allocation.
    /// </summary>
    /// <param name="input">The raw query text (slot value).</param>
    /// <returns>The romanized query, or the input unchanged when it has no kana.</returns>
    internal static string Romanize(string input)
    {
        if (string.IsNullOrEmpty(input) || !ContainsKana(input))
        {
            return input;
        }

        var sb = new StringBuilder(input.Length * 3);
        for (int i = 0; i < input.Length; i++)
        {
            char key = ToKatakanaKey(input[i]);

            if (key == LongVowelMark)
            {
                // Contraction: the preceding vowel already carries the length.
                continue;
            }

            if (key == SmallTsu)
            {
                // Geminate: double the next syllable's consonant ('t' before 'ch').
                // A sokuon before a vowel or non-kana character is not a real
                // geminate shape and is dropped.
                if (TryMapSyllable(input, i + 1, out string? next, out _)
                    && next.Length > 0 && IsConsonant(next[0]))
                {
                    sb.Append(next[0] == 'c' && next.StartsWith("ch", StringComparison.Ordinal) ? 't' : next[0]);
                }

                continue;
            }

            if (TryMapSyllable(input, i, out string? syllable, out int consumed))
            {
                sb.Append(syllable);
                i += consumed - 1;
                continue;
            }

            sb.Append(input[i]);
        }

        return sb.ToString();
    }

    /// <summary>Whether the string contains any hiragana or katakana character.
    /// Callers use this to gate kana-specific recovery tiers (JF-643 genre
    /// resolution) so Latin queries keep their exact behavior.</summary>
    internal static bool ContainsKana(string input)
    {
        foreach (char c in input)
        {
            if (IsHiragana(c) || IsKatakana(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHiragana(char c) => c >= 'ぁ' && c <= 'ゖ';

    private static bool IsKatakana(char c) => c >= 'ァ' && c <= 'ヿ';

    /// <summary>
    /// Folds a hiragana character to its katakana code point (constant offset
    /// 0x60 across the whole letter range); every other character is its own key.
    /// </summary>
    private static char ToKatakanaKey(char c) => IsHiragana(c) ? (char)(c + 0x60) : c;

    private static bool IsConsonant(char c) =>
        char.IsLetter(c) && c is not ('a' or 'e' or 'i' or 'o' or 'u');

    /// <summary>
    /// Maps the syllable starting at <paramref name="index"/> (digraph first,
    /// then single character). Returns false for non-kana positions.
    /// </summary>
    /// <param name="input">The full input string.</param>
    /// <param name="index">The position to map from.</param>
    /// <param name="romaji">The mapped romaji syllable.</param>
    /// <param name="consumed">How many characters the syllable consumed (1 or 2).</param>
    private static bool TryMapSyllable(string input, int index, [NotNullWhen(true)] out string? romaji, out int consumed)
    {
        romaji = null;
        consumed = 0;
        if (index >= input.Length)
        {
            return false;
        }

        char first = ToKatakanaKey(input[index]);
        if (index + 1 < input.Length)
        {
            char second = ToKatakanaKey(input[index + 1]);
            if (Digraphs.TryGetValue((first, second), out string? digraph))
            {
                romaji = digraph;
                consumed = 2;
                return true;
            }
        }

        if (Syllables.TryGetValue(first, out string? syllable))
        {
            romaji = syllable;
            consumed = 1;
            return true;
        }

        return false;
    }
}
