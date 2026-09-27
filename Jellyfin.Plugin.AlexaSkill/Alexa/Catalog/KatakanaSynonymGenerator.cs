#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// JF-646: generates KATAKANA renderings of Latin artist/album names for the ja-JP
/// catalog upload, so Amazon's NLU resolves naturalized Japanese voice ('クイーン')
/// against the JellyfinArtist/AlbumName catalog values via entity resolution and
/// artist carriers WIN selection the way en-US/hi-IN do (the JF-642 finding: ja's
/// katakana musician slot loses selection to the free-text genre slot unless catalog
/// ER carries the kana form). Coverage-oriented like the Romance generators (JF-362):
/// emit the plausible spoken forms; entity resolution needs one hit, so extra
/// near-miss synonyms are harmless. The goal is COVERAGE, not dictionary-perfect
/// loanword orthography.
/// Syllable values come from KatakanaRomanizer's syllabary in reverse
/// (<see cref="KatakanaRomanizer.TryKatakana"/>) - the SAME table the query-side
/// romanizer uses, so Latin -> kana -> romaji is consistent by construction. The
/// load-bearing round-trip property: each long vowel is ONE 'ー' (never two),
/// matching the romanizer's contraction, so 'Queen' -> 'クイーン' -> 'kuin' (the
/// JF-643 Double Metaphone bridge), never 'クイーーン'.
/// The walk is spelling-driven (English orthography -> mora units), so it owns its
/// own digraph rules (th/gh/ea/ee/silent-e/vowel-r...); those key on Latin letter
/// sequences and have no counterpart in the kana-keyed syllabary, which is why a
/// separate rule table exists beside the shared syllable VALUES. Ambiguous shapes
/// emit one whole-name alternate variant: 'ea' as /iː/ ('ビートルズ') vs /ɛ/
/// ('ヘッド'-shape), v as the traditional b-line vs the modern ヴ-line, letter-adjacent
/// ti as チ vs ティ, and the クイ vs クィ loanword spellings.
/// Unlike JapanesePhoneticSynonyms this does NOT skip Japanese-origin Latin names:
/// 'Utada' naturalizes to ウタダ, which is exactly what a ja user says, so the kana
/// form helps ER there too (the Latin-to-Latin generator skips them because its
/// romaji mangling of an already-romaji name adds nothing).
/// </summary>
public static class KatakanaSynonymGenerator
{
    private const char LongVowelMark = 'ー';
    private const char SmallTsu = 'ッ';
    private const char KatakanaMiddleDot = '・';
    private const string ArticleKana = "ザ";
    private const string SmallI = "ィ";
    private const string SmallE = "ェ";

    /// <summary>
    /// Generates the katakana rendering(s) of a Latin name: the standard form,
    /// an article-dropped form for 'The ' names, the ambiguous-shape alternate,
    /// and a space-joined form of multi-word names (ja ASR transcribes the pause
    /// as a space or a middle dot). Order is device-capture priority; the caller
    /// (PhoneticSynonymGenerator's ja arm) dedups and applies the per-name cap.
    /// </summary>
    /// <param name="name">The artist or album name.</param>
    /// <returns>Katakana variant strings, empty for names with no letters or already-kana names.</returns>
#pragma warning disable CA1002 // Collection return type is intentional for caller convenience
    public static List<string> Generate(string name)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        // Already naturalized: the name itself is the spoken form ER matches.
        if (KatakanaRomanizer.ContainsKana(trimmed))
        {
            return new List<string>();
        }

        List<string> words = SplitWords(trimmed);
        if (words.Count == 0)
        {
            return new List<string>();
        }

        bool hasArticle = string.Equals(words[0], "the", StringComparison.Ordinal);
        List<string> contentWords = hasArticle ? words.Skip(1).ToList() : words;
        if (contentWords.Count == 0)
        {
            return new List<string>();
        }

        var primaryWords = new List<string>(contentWords.Count);
        var altWords = new List<string>(contentWords.Count);
        bool anyAlt = false;
        foreach (string word in contentWords)
        {
            string primary = TransliterateWord(word, altForms: false);
            string alt = TransliterateWord(word, altForms: true);
            primaryWords.Add(primary);
            altWords.Add(alt);
            if (!string.Equals(alt, primary, StringComparison.Ordinal))
            {
                anyAlt = true;
            }
        }

        var results = new List<string>();
        string joined = string.Join(KatakanaMiddleDot, primaryWords);
        results.Add(hasArticle ? ArticleKana + KatakanaMiddleDot + joined : joined);

        if (hasArticle)
        {
            // Japanese usually drops 'The' ('ビートルズ'); ASR captures both shapes.
            results.Add(joined);
        }

        if (anyAlt)
        {
            string altJoined = string.Join(KatakanaMiddleDot, altWords);
            results.Add(hasArticle ? ArticleKana + KatakanaMiddleDot + altJoined : altJoined);
        }

        if (hasArticle || primaryWords.Count > 1)
        {
            // Space-separated rendering: ja ASR transcribes the word pauses (and
            // the article) as spaces or middle dots ("ザ スミス" / "ザ・スミス").
            results.Add((hasArticle ? ArticleKana + " " : string.Empty) + string.Join(" ", primaryWords));
        }

        return results.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> SplitWords(string name)
    {
        // Letter runs only, split on spaces and punctuation ('/' '&', '!'): an
        // apostrophe BETWEEN letters is intraword and dropped, not a split
        // ("D'Angelo" -> "dangelo"; "Simon & Garfunkel" -> two words).
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsAsciiLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == '\'')
            {
                continue;
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }

        return words;
    }

    private static string TransliterateWord(string word, bool altForms)
    {
        var builder = new MoraBuilder();
        int i = 0;
        while (i < word.Length)
        {
            i = char.IsAsciiLetter(word[i]) && !IsVowelLetter(word[i])
                ? ConsumeConsonant(word, i, altForms, builder)
                : ConsumeVowel(word, i, altForms, builder);
        }

        return string.Concat(builder.Pieces);
    }

    // --- vowel units ---

    private static int ConsumeVowel(string word, int i, bool alt, MoraBuilder builder)
    {
        (List<(string Key, bool Long)> morae, int consumed) = ParseVowelUnit(word, i, alt);
        builder.EmitVowelUnit(morae);
        return i + consumed;
    }

    /// <summary>
    /// Parses the vowel unit starting at <paramref name="i"/> into mora keys
    /// (romaji syllable values the shared syllabary maps back to kana). English
    /// vowel teams and the silent-e / vowel-r lengthenings each yield either one
    /// long mora (the 'ー' carrier) or a diphthong's two plain morae.
    /// </summary>
    private static (List<(string Key, bool Long)> Morae, int Consumed) ParseVowelUnit(string word, int i, bool alt)
    {
        int len = word.Length;
        char v = word[i];
        char next = i + 1 < len ? word[i + 1] : '\0';
        char after = i + 2 < len ? word[i + 2] : '\0';

        // "igh" is /aɪ/ ("light" -> ライト).
        if (v == 'i' && next == 'g' && after == 'h')
        {
            return (new List<(string, bool)> { ("a", false), ("i", false) }, 3);
        }

        // Lone trailing 'e' is silent ("stone", "keane").
        if (v == 'e' && i == len - 1 && i > 0)
        {
            return (new List<(string, bool)>(), 1);
        }

        if (next == 'e' && v == 'e')
        {
            return (new List<(string, bool)> { ("i", true) }, 2);
        }

        if (next == 'a' && v == 'e')
        {
            // 'ea' is /iː/ ("beat") but /ɛ/ in the head/death family; the /ɛ/
            // reading is the whole-name alternate variant.
            return (alt
                ? new List<(string, bool)> { ("e", false) }
                : new List<(string, bool)> { ("i", true) }, 2);
        }

        if (next == 'o' && v == 'o')
        {
            return (new List<(string, bool)> { ("u", true) }, 2);
        }

        if (next == 'a' && v == 'o')
        {
            return (new List<(string, bool)> { ("o", true) }, 2);
        }

        if (next == 'i' && v == 'a')
        {
            // Word-initial "ai" keeps /aɪ/ ("Aida" -> アイダ); medial "ai" is /eɪ/.
            return (i == 0
                ? new List<(string, bool)> { ("a", false), ("i", false) }
                : new List<(string, bool)> { ("e", false), ("i", false) }, 2);
        }

        if (next == 'y' && v == 'a')
        {
            return (new List<(string, bool)> { ("e", false), ("i", false) }, 2);
        }

        if ((next == 'i' && v == 'o') || (next == 'y' && v == 'o'))
        {
            return (new List<(string, bool)> { ("o", false), ("i", false) }, 2);
        }

        if ((next == 'u' && v == 'a') || (next == 'w' && v == 'a'))
        {
            return (new List<(string, bool)> { ("o", true) }, 2);
        }

        if (next == 'u' && v == 'o')
        {
            return (new List<(string, bool)> { ("o", false), ("u", false) }, 2);
        }

        // Vowel + r before a consonant or the end r-colors to a long vowel
        // ("her" -> ハー, "born" -> ボーン); 'or' keeps 'o', the rest drift to 'a'.
        // A word-final 'y' after the r is a vowel ("mary"), not a consonant.
        if (next == 'r'
            && (after == '\0' || (IsConsonantLetter(after) && !(after == 'y' && i + 2 == len - 1))))
        {
            return (new List<(string, bool)> { (v == 'o' ? "o" : "a", true) }, 2);
        }

        // Short-i + nd/ld is /aɪnd/ ("mind" -> マインド, "wild" -> ワイルド).
        if (v == 'i' && after == 'd' && (next == 'n' || next == 'l'))
        {
            return (new List<(string, bool)> { ("a", false), ("i", false) }, 1);
        }

        // Vowel + single consonant + final 'e' is the silent-e lengthening
        // ("stone" -> ストーン); the consonant is parsed next and the 'e' drops above.
        if (IsConsonantLetter(next) && after == 'e' && i + 3 == len)
        {
            return (new List<(string, bool)> { (v.ToString(), true) }, 1);
        }

        return (new List<(string, bool)> { (v.ToString(), false) }, 1);
    }

    // --- consonant units ---

    private static int ConsumeConsonant(string word, int i, bool alt, MoraBuilder builder)
    {
        int len = word.Length;
        char c = word[i];
        char next = i + 1 < len ? word[i + 1] : '\0';

        // Moraic nasal: n/m before another consonant (never before a vowel or y).
        if (c == 'n' && (next == '\0' || (IsConsonantLetter(next) && next != 'y')))
        {
            builder.EmitNasal();
            return i + 1;
        }

        if (c == 'm' && next != '\0' && IsConsonantLetter(next) && next != 'y')
        {
            builder.EmitNasal();
            return i + 1;
        }

        ReadOnlySpan<char> rest = word.AsSpan(i);
        if (rest.StartsWith("tion", StringComparison.Ordinal) || rest.StartsWith("sion", StringComparison.Ordinal))
        {
            builder.EmitSyllable("sho");
            builder.EmitNasal();
            return i + 4;
        }

        if (rest.StartsWith("tch", StringComparison.Ordinal))
        {
            builder.RequestGeminate();
            return ProcessSound(word, i + 3, "ch", alt, builder);
        }

        if (rest.StartsWith("sch", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 3, "sh", alt, builder);
        }

        if (rest.StartsWith("dge", StringComparison.Ordinal) && i + 3 == len)
        {
            builder.RequestGeminate();
            return ProcessSound(word, i + 3, "j", alt, builder);
        }

        if (rest.StartsWith("ch", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "ch", alt, builder);
        }

        if (rest.StartsWith("sh", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "sh", alt, builder);
        }

        // th: voiced only between vowels ("weather"-class shapes); voiceless
        // elsewhere ("smiths" keeps /s/ even after a vowel because a consonant
        // follows). The article word is special-cased before this walk runs.
        if (rest.StartsWith("th", StringComparison.Ordinal))
        {
            bool intervocalic = i > 0 && IsVowelLetter(word[i - 1])
                && i + 2 < len && IsVowelLetter(word[i + 2]);
            return ProcessSound(word, i + 2, intervocalic ? "z" : "s", alt, builder);
        }

        if (rest.StartsWith("ph", StringComparison.Ordinal) || rest.StartsWith("gh", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "f", alt, builder);
        }

        if (rest.StartsWith("ck", StringComparison.Ordinal))
        {
            builder.RequestGeminate();
            return ProcessSound(word, i + 2, "k", alt, builder);
        }

        if (rest.StartsWith("qu", StringComparison.Ordinal))
        {
            return ConsumeQu(word, i, alt, builder);
        }

        if (i == 0 && rest.StartsWith("kn", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "n", alt, builder);
        }

        if (i == 0 && rest.StartsWith("wr", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "r", alt, builder);
        }

        if (rest.StartsWith("wh", StringComparison.Ordinal))
        {
            return ProcessSound(word, i + 2, "w", alt, builder);
        }

        // Dark l: word-final 'le' or 'les' is its own mora ("beatles" ->
        // ビートルズ, "simple" -> シンプル). Restricted to these shapes because a
        // mid-word "le" before another consonant is a plain CV ("led" is レッド,
        // never ルド).
        if (c == 'l' && next == 'e' && (i + 2 >= len || word[i + 2] == 's'))
        {
            builder.EmitSyllable("ru");
            return i + 2;
        }

        // Word-final single 's': voiced ズ after a vowel or y ("beatles" ->
        // ビートルズ, "jones" -> ジョーンズ), plain ス after another consonant,
        // and merged away entirely when a sibilant digraph just sounded
        // ("smiths" -> スミス).
        if (c == 's' && i + 1 == len && i > 0)
        {
            if (word[i - 1] == 'h')
            {
                return i + 1;
            }

            builder.EmitSyllable(IsVowelLetter(word[i - 1]) || word[i - 1] == 'y' ? "zu" : "su");
            return i + 1;
        }

        // Doubled consonant letters are one geminate consonant ("happy" ->
        // ハッピー), except a final 'zz' which never geminates ("jazz" -> ジャズ).
        if (next == c && IsConsonantLetter(c))
        {
            if (!(c == 'z' && i + 2 == len))
            {
                builder.RequestGeminate();
            }

            return ProcessSound(word, i + 2, SoundOfLetter(c, word, i + 2, alt), alt, builder);
        }

        if (c == 'x')
        {
            // x unfolds as k+s ("max" -> マックス, "maxwell" -> マックスウェル).
            int afterX = ProcessSound(word, i + 1, "k", alt, builder);
            return ProcessSound(word, afterX, "s", alt, builder);
        }

        return ProcessSound(word, i + 1, SoundOfLetter(c, word, i + 1, alt), alt, builder);
    }

    /// <summary>
    /// 'qu': the k sound gets its own ク mora and the following vowel unit stands
    /// as its own mora(e) ("queen" -> ク・イー・ン). The modern loanword spelling
    /// folds i/e into the small-kana glide instead (クィーン/クェ); those composed
    /// forms have no key in the shared syllabary, and the romanizer folds them
    /// back to the plain vowel values on the way home, so both spellings
    /// round-trip identically.
    /// </summary>
    private static int ConsumeQu(string word, int i, bool alt, MoraBuilder builder)
    {
        int len = word.Length;
        if (alt && i + 2 < len && word[i + 2] is 'i' or 'e')
        {
            (List<(string Key, bool Long)> morae, int consumed) = ParseVowelUnit(word, i + 2, alt);
            if (morae.Count > 0 && KatakanaRomanizer.TryKatakana("ku", out string? ku))
            {
                // The small vowel follows the unit's parsed sound ("queen"'s 'ee'
                // is /iː/, so クィ not クェ).
                builder.EmitRaw(ku, morae[0].Key == "e" ? SmallE : SmallI, morae[0].Long);
                builder.MarkVowelShape(morae.Count, morae[0].Long);
                foreach (var (key, longV) in morae.Skip(1))
                {
                    builder.EmitSyllable(key, longV);
                }

                return i + 2 + consumed;
            }
        }

        builder.EmitSyllable("ku");
        return i + 2;
    }

    /// <summary>
    /// Resolves a single consonant letter to its sound, using the letter that
    /// follows for the c/g softness split and the v/b loanword choice.
    /// </summary>
    private static string SoundOfLetter(char c, string word, int nextIndex, bool alt)
    {
        char next = nextIndex < word.Length ? word[nextIndex] : '\0';
        return c switch
        {
            'c' => next is 'e' or 'i' or 'y' ? "s" : "k",
            'g' => next is 'e' or 'i' or 'y' ? "j" : "g",
            'v' => alt ? "v" : "b",
            'l' => "r",
            'q' => "k",
            _ => c.ToString()
        };
    }

    /// <summary>
    /// Emits a resolved consonant sound at <paramref name="pos"/> (the index
    /// after its letters): composed with the following vowel unit when one is
    /// there, as an epenthetic mora when a consonant follows, or by the
    /// word-final rules at the end of the word. Returns the new scan index.
    /// </summary>
    private static int ProcessSound(string word, int pos, string sound, bool alt, MoraBuilder builder)
    {
        int len = word.Length;
        if (pos >= len)
        {
            EmitFinalConsonant(sound, builder);
            return pos;
        }

        char next = word[pos];
        if (IsVowelLetter(next))
        {
            (List<(string Key, bool Long)> morae, int consumed) = ParseVowelUnit(word, pos, alt);
            if (morae.Count == 0)
            {
                // The unit was a lone trailing silent 'e' ("keane", "stone"): the
                // consonant is word-final, not composed.
                EmitFinalConsonant(sound, builder);
                return pos + consumed;
            }

            EmitComposed(builder, sound, morae, alt);
            return pos + consumed;
        }

        if (next == 'y')
        {
            return ConsumeConsonantPlusY(word, pos, sound, alt, builder);
        }

        builder.EmitSyllable(EpentheticKey(sound));
        return pos;
    }

    /// <summary>
    /// A consonant followed by y: a final y in a MONOSYLLABLE is the /aɪ/ unit
    /// ("sky" -> スカイ, "my" -> マイ), a final y in a longer word is /i/ with the
    /// modern yoon keys ("city" -> シティ, "party" -> パーティ), a y before a
    /// consonant is a plain i mora ("lynn" -> リン), and a y before a vowel is a
    /// semivowel compose ("tokyo"-class キョ).
    /// </summary>
    private static int ConsumeConsonantPlusY(string word, int pos, string sound, bool alt, MoraBuilder builder)
    {
        int len = word.Length;
        if (pos == len - 1)
        {
            if (IsMonosyllableTail(word, pos))
            {
                EmitComposed(builder, sound, new List<(string Key, bool Long)> { ("a", false), ("i", false) }, alt);
            }
            else
            {
                builder.EmitSyllable(YoonKey(sound, 'i') ?? ComposeKey(sound, "i", alt));
                builder.MarkVowelShape(1, false);
            }

            return pos + 1;
        }

        if (!IsVowelLetter(word[pos + 1]))
        {
            EmitComposed(builder, sound, new List<(string Key, bool Long)> { ("i", false) }, alt);
            return pos + 1;
        }

        // Semivowel: compose through the y with the vowel after it.
        char vowel = word[pos + 1];
        (List<(string Key, bool Long)> morae, int consumed) = ParseVowelUnit(word, pos + 1, alt);
        if (morae.Count == 0)
        {
            builder.EmitSyllable(EpentheticKey(sound));
            return pos + 1 + consumed;
        }

        string first = YoonKey(sound, vowel) ?? sound + "y" + vowel;
        if (KatakanaRomanizer.TryKatakana(first, out _))
        {
            builder.EmitSyllable(first, morae[0].Long);
        }
        else
        {
            // No yoon key for this shape ("kyi"); fall back to the plain CV and
            // let the vowel stand as its own mora.
            EmitComposed(builder, sound, morae, alt);
            return pos + 1;
        }

        foreach (var (key, longV) in morae.Skip(1))
        {
            builder.EmitSyllable(key, longV);
        }

        builder.MarkVowelShape(morae.Count, morae[0].Long);
        return pos + 1 + consumed;
    }

    /// <summary>
    /// The y-composed syllable keys for the sounds whose yoon differs from a
    /// plain CV ("city" -> シティ, "tokyo"-class キョ via the caller's fallback).
    /// t/d + i are ALWAYS the modern ティ/ディ in a y shape, unlike the
    /// letter-adjacent "ti" where <see cref="ComposeKey"/> applies the
    /// traditional チ to the primary variant: English -ty-/-dy- transcribes with
    /// ティ/ディ even in traditional loanwords ("party" -> パーティ), so the
    /// alternate pass has nothing to swap there. Returns null for other sounds;
    /// callers supply their own fallback.
    /// </summary>
    private static string? YoonKey(string sound, char vowel) => sound switch
    {
        "t" => vowel switch { 'a' => "cha", 'i' => "ti", 'u' => "chu", 'e' => "che", _ => "cho" },
        "d" => vowel switch { 'a' => "ja", 'i' => "di", 'u' => "ju", 'e' => "je", _ => "jo" },
        "s" => vowel switch { 'a' => "sha", 'i' => "shi", 'u' => "shu", 'e' => "she", _ => "sho" },
        _ => null
    };

    /// <summary>
    /// True when the consonant run ending at <paramref name="pos"/> - 1 reaches
    /// the word start, the monosyllable shape whose final y is /aɪ/ ("sky");
    /// a run stopped by an earlier vowel means a longer word whose final y is
    /// /i/ ("city").
    /// </summary>
    private static bool IsMonosyllableTail(string word, int pos)
    {
        int i = pos - 1;
        while (i >= 0 && !IsVowelLetter(word[i]))
        {
            i--;
        }

        return i < 0;
    }

    /// <summary>Composes a consonant sound with a vowel unit's morae and emits them.</summary>
    private static void EmitComposed(MoraBuilder builder, string sound, List<(string Key, bool Long)> morae, bool alt)
    {
        if (morae.Count == 0)
        {
            return;
        }

        builder.EmitSyllable(ComposeKey(sound, morae[0].Key, alt), morae[0].Long);
        foreach (var (key, longV) in morae.Skip(1))
        {
            builder.EmitSyllable(key, longV);
        }

        builder.MarkVowelShape(morae.Count, morae[0].Long);
    }

    /// <summary>
    /// The composed CV syllable key, with the Japanese-only redirects the shared
    /// syllabary already encodes in the kana direction (si->shi, letter-adjacent
    /// ti->chi or the modern ティ alternate, tu->tsu, hu->fu, du->ドゥ) and the
    /// w/y vowel inventories.
    /// </summary>
    private static string ComposeKey(string sound, string vowel, bool alt)
    {
        if (sound == "s" && vowel == "i")
        {
            return "shi";
        }

        if (sound == "z" && vowel == "i")
        {
            return "ji";
        }

        if (sound == "t" && vowel == "i")
        {
            return alt ? "ti" : "chi";
        }

        if (sound == "t" && vowel == "u")
        {
            return "tsu";
        }

        if (sound == "h" && vowel == "u")
        {
            return "fu";
        }

        if (sound == "d" && vowel == "u")
        {
            return "du";
        }

        if (sound == "w")
        {
            return vowel switch
            {
                "a" => "wa",
                "i" => "wi",
                "e" => "we",
                "o" => "wo",
                _ => "u"
            };
        }

        if (sound == "y")
        {
            return vowel switch
            {
                "a" => "ya",
                "u" => "yu",
                "o" => "yo",
                _ => "i"
            };
        }

        return sound + vowel;
    }

    /// <summary>
    /// Word-final consonant: the checked stops t/d/k/g geminate after a single
    /// short vowel ("bit" -> ビット, "rock" -> ロック) and take their epenthetic
    /// mora otherwise ("beat" -> ビート). An s reaching this path (from the x
    /// unfold or a doubled 'ss') is voiceless ス; the single-letter final s with
    /// its voiced/merged variants is decided in ConsumeConsonant where the
    /// preceding letter is in hand.
    /// </summary>
    private static void EmitFinalConsonant(string sound, MoraBuilder builder)
    {
        if (sound == "s")
        {
            builder.EmitSyllable("su");
            return;
        }

        if (sound == "z")
        {
            builder.EmitSyllable("zu");
            return;
        }

        if (sound == "n")
        {
            builder.EmitNasal();
            return;
        }

        if (sound is "t" or "d" or "k" or "g" && builder.LastWasSingleShortVowel)
        {
            builder.RequestGeminate();
        }

        builder.EmitSyllable(EpentheticKey(sound));
    }

    /// <summary>The default vowel a lone consonant takes (o for t/d, u for the rest).</summary>
    private static string EpentheticKey(string sound) => sound switch
    {
        "t" => "to",
        "d" => "do",
        "h" => "fu",
        "sh" => "shi",
        "ch" => "chi",
        "j" => "ji",
        "w" => "u",
        "y" => "yu",
        "n" => "n",
        _ => sound + "u"
    };

    private static bool IsVowelLetter(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static bool IsConsonantLetter(char c) => char.IsAsciiLetter(c) && !IsVowelLetter(c);

    /// <summary>
    /// Accumulates kana pieces, owning the two context-dependent decisions that
    /// depend on what was emitted before: the pending geminate (ッ, applied by
    /// the next syllable) and the single-short-vowel flag that gates the
    /// word-final stop gemination ("bit" ビット vs "beat" ビート).
    /// </summary>
    private sealed class MoraBuilder
    {
        public List<string> Pieces { get; } = new();

        private bool _pendingGeminate;
        private bool _lastWasSingleShortVowel;

        public bool LastWasSingleShortVowel => _lastWasSingleShortVowel;

        public void EmitSyllable(string key, bool longVowel = false)
        {
            if (!KatakanaRomanizer.TryKatakana(key, out string? kana))
            {
                // Every key the composers emit is a member of the shared syllabary
                // (pinned by tests); skipping is a defensive backstop so catalog
                // sync can never crash or emit Latin inside a kana synonym.
                return;
            }

            AppendCore(kana, longVowel);
        }

        public void EmitRaw(string kana, string smallKana, bool longVowel)
        {
            AppendCore(kana, longVowel, smallKana);
        }

        public void EmitNasal()
        {
            Pieces.Add("ン");
            _lastWasSingleShortVowel = false;
        }

        private void AppendCore(string kana, bool longVowel, string? extraKana = null)
        {
            if (_pendingGeminate)
            {
                Pieces.Add(SmallTsu.ToString());
                _pendingGeminate = false;
            }

            Pieces.Add(kana);
            if (extraKana is not null)
            {
                Pieces.Add(extraKana);
            }

            if (longVowel)
            {
                Pieces.Add(LongVowelMark.ToString());
            }

            _lastWasSingleShortVowel = false;
        }

        public void EmitVowelUnit(List<(string Key, bool Long)> morae)
        {
            foreach (var (key, longV) in morae)
            {
                EmitSyllable(key, longV);
            }

            MarkVowelShape(morae.Count, morae.Count > 0 && morae[0].Long);
        }

        public void MarkVowelShape(int moraCount, bool anyLong)
        {
            _lastWasSingleShortVowel = moraCount == 1 && !anyLong;
        }

        public void RequestGeminate() => _pendingGeminate = true;
    }
}
