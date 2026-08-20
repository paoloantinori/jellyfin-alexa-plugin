#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Tokenizes song titles and user keyword input, then scores matches
/// using keyword coverage and title coverage with a positional bonus.
/// Designed for conversational song search where users provide partial
/// keywords rather than exact titles.
/// </summary>
internal static class KeywordMatcher
{
    /// <summary>
    /// Score bonus applied when user keywords match starting from the first title token.
    /// This preferentially ranks songs whose title begins with the user's query.
    /// </summary>
    private const double PositionalBonus = 5.0;

    /// <summary>
    /// Weight for keyword coverage in the score formula (fraction of user keywords found in title).
    /// </summary>
    private const double KeywordCoverageWeight = 0.7;

    /// <summary>
    /// Weight for title coverage in the score formula (fraction of title tokens covered by user keywords).
    /// </summary>
    private const double TitleCoverageWeight = 0.3;

    /// <summary>
    /// Penalty multiplier applied to phonetic match scores. Phonetic matches are inherently
    /// less certain than exact matches, so they receive a lower score to avoid false positives
    /// outranking legitimate exact matches in mixed-result scenarios.
    /// </summary>
    private const double PhoneticPenalty = 0.75;

    /// <summary>
    /// Minimum keyword coverage required for phonetic matching. Relaxed from 1.0 (100%)
    /// to 0.5 (50%) because phonetic encoding collapses different spellings into the same
    /// code, which can cause false keyword-to-title matches. Requiring all keywords to match
    /// phonetically would be too strict when the user misspells multiple words differently.
    /// </summary>
    private const double MinPhoneticKeywordCoverage = 0.5;

    /// <summary>
    /// Stop words keyed by locale prefix (e.g. "en" for en-US, en-GB, etc.).
    /// Unknown locale prefixes default to an empty set.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "in", "on", "at", "to", "by", "from", "and", "or", "is", "it"
        },
        ["it"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "il", "lo", "la", "i", "gli", "le", "di", "del", "della",
            "un", "una", "in", "su", "per", "con", "da", "e", "o", "che"
        },
        ["de"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "der", "die", "das", "ein", "eine", "und", "oder",
            "in", "an", "auf", "zu", "von", "mit"
        },
        ["fr"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "le", "la", "les", "un", "une", "des", "de", "du",
            "en", "dans", "sur", "et", "ou"
        },
        ["es"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "el", "la", "los", "las", "un", "una", "unos", "unas",
            "de", "del", "al", "en", "con", "por", "para", "y", "o"
        },
        ["pt"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "o", "a", "os", "as", "um", "uma", "uns", "umas",
            "de", "do", "da", "dos", "das", "no", "na", "nos", "nas",
            "em", "com", "por", "para", "e", "ou"
        }
    };

    /// <summary>
    /// Canonicalizes common title-word abbreviations (JF-383) so a spoken full word
    /// ("street") matches an abbreviated tagged token ("St.") and vice versa. Music taggers
    /// frequently abbreviate these words in track titles. Applied inside
    /// <see cref="Tokenize"/> so BOTH sides (title index and user keywords) canonicalize
    /// identically, making the match bidirectional. "saint" shares the st class because
    /// tagged "St." is ambiguous between Street and Saint ("Decatur St." vs "St. Louis
    /// Blues"); the canonical token is a class representative, not a semantic claim, and
    /// the extra cross-matching is acceptable in this coverage-oriented search.
    /// number/no is deliberately EXCLUDED: "no" is a real word in English/Italian and a
    /// grammatical particle in Japanese (e.g. "watashi no uta"), so canonicalizing it
    /// would corrupt token streams.
    /// LOAD-BEARING INVARIANT: no canonical OUTPUT may be a stop word in any locale
    /// (canonicalization runs after the stop-word filter), otherwise matching would
    /// break asymmetrically: the abbreviated title token would survive Tokenize while
    /// the spoken full word would be stop-word-filtered on the keyword side. None of
    /// street/road/avenue/part/volume is a stop word today; keep it that way when
    /// extending this map.
    /// </summary>
    private static readonly Dictionary<string, string> AbbreviationCanonicalForms = new(StringComparer.Ordinal)
    {
        ["st"] = "street",
        ["saint"] = "street",
        ["rd"] = "road",
        ["ave"] = "avenue",
        ["pt"] = "part",
        ["vol"] = "volume"
    };

    private static string CanonicalizeAbbreviation(string token) =>
        AbbreviationCanonicalForms.TryGetValue(token, out string? canonical) ? canonical : token;

    /// <summary>
    /// The English stop-word set, hoisted for <see cref="Tokenize"/> (JF-384: always
    /// stripped, in addition to the locale set, because English titles spoken under
    /// non-English locales carry English function words). The "en" key is guaranteed
    /// by the <see cref="StopWords"/> initializer.
    /// </summary>
    private static readonly HashSet<string> EnglishStopWords = StopWords["en"];

    /// <summary>
    /// Lowercases a raw token and adds it unless it is a stop word of the request locale
    /// OR of English (see the JF-384 note in <see cref="Tokenize"/>).
    /// </summary>
    private static void AddIfNotStopWord(
        string raw,
        HashSet<string>? localeStopWords,
        HashSet<string> englishStopWords,
        List<string> tokens)
    {
        string token = raw.ToLowerInvariant();
        if (!(localeStopWords?.Contains(token) ?? false) && !englishStopWords.Contains(token))
        {
            tokens.Add(token);
        }
    }

    /// <summary>
    /// Tokenizes the input text by lowercasing, splitting on whitespace and punctuation,
    /// removing locale-specific stop words, and canonicalizing title-word abbreviations
    /// (see <see cref="AbbreviationCanonicalForms"/>).
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="locale">The locale string (e.g. "en-US") used to resolve stop words.</param>
    /// <returns>An array of non-stop-word, abbreviation-canonicalized tokens, lowercased. Empty array for null, empty, or stop-words-only input.</returns>
    public static string[] Tokenize(string? text, string locale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        string prefix = GetLocalePrefix(locale);
        HashSet<string>? stopWordSet = null;
        if (!string.IsNullOrEmpty(prefix))
        {
            StopWords.TryGetValue(prefix, out stopWordSet);
        }

        // JF-384: music titles are mostly English, so an English title spoken under a
        // NON-English locale carries English function words the locale list does not
        // strip (it-IT keeps "the"). Always strip the English set too (EnglishStopWords):
        // symmetric with the n-gram index (built with "en-US"), and English function
        // words are never meaningful match keywords. Harmless for en itself (same set).
        // RESIDUAL ASYMMETRY (known, accepted): the index side keeps NON-English stop
        // words (it is built with en-US), so an Italian title like "Il Sole" indexes as
        // [il, sole] while an it-IT query tokenizes to [sole]. This is harmless: the
        // index's extra function words only widen the candidate lookup keys, and ranking
        // always re-tokenizes the title with the request locale (Score/ScorePhonetic),
        // so coverage is computed on identical token streams. Do not "fix" by
        // union-stripping the index: locale stop-word sets collide with real title words
        // across languages ("da" in "Da Vinci", "la" as a musical note).
        // Split on any character that is not a letter or digit
        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0)
                {
                    AddIfNotStopWord(text.Substring(start, i - start), stopWordSet, EnglishStopWords, tokens);
                    start = -1;
                }
            }
        }

        // Handle trailing token
        if (start >= 0)
        {
            AddIfNotStopWord(text[start..], stopWordSet, EnglishStopWords, tokens);
        }

        // Canonicalize abbreviations post-filter (JF-383): safe because no canonical
        // output is a stop word (see the invariant on AbbreviationCanonicalForms).
        for (int i = 0; i < tokens.Count; i++)
        {
            tokens[i] = CanonicalizeAbbreviation(tokens[i]);
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// Exact keyword scoring with the JF-384 phonetic fallback: if the exact matcher
    /// (100% keyword coverage) returns nothing and the caller's phonetic flag is on,
    /// re-scores the SAME candidate set phonetically (>=50% keyword coverage + penalty),
    /// so accent drift on one keyword ("Decature" heard as "cater") does not veto the
    /// match. The single place the flag + fallback semantics live (mirrors the global
    /// n-gram path's stage 1 -> 2 chain). A single garbage keyword still misses both.
    /// </summary>
    /// <param name="songs">Candidate songs to score (bounded set, e.g. one artist's).</param>
    /// <param name="keywordTokens">Pre-tokenized user keywords (from <see cref="Tokenize"/>).</param>
    /// <param name="locale">The locale string for tokenizing song titles.</param>
    /// <param name="phoneticEnabled">Whether the phonetic fallback stage may run
    /// (caller's PhoneticSongSearchEnabled).</param>
    /// <returns>Exact-match results if any, else phonetic results, else empty.</returns>
    public static List<(BaseItem Item, double Score)> ScoreWithPhoneticFallback(
        IReadOnlyList<BaseItem> songs, string[] keywordTokens, string locale, bool phoneticEnabled)
    {
        var exact = Score(songs, keywordTokens, locale);
        if (exact.Count > 0 || !phoneticEnabled)
        {
            return exact;
        }

        return ScorePhonetic(songs, keywordTokens, locale);
    }

    /// <summary>
    /// Scores a list of candidate songs against user keyword tokens.
    /// Only songs where all user keywords appear in the title (keywordCoverage == 1.0) are included.
    /// Results are sorted by score descending.
    /// </summary>
    /// <param name="songs">Candidate songs to score.</param>
    /// <param name="keywordTokens">Pre-tokenized user keywords (from <see cref="Tokenize"/>).</param>
    /// <param name="locale">The locale string for tokenizing song titles.</param>
    /// <returns>List of (Item, Score) tuples sorted by score descending. Empty if no matches or empty inputs.</returns>
    public static List<(BaseItem Item, double Score)> Score(
        IReadOnlyList<BaseItem> songs, string[] keywordTokens, string locale)
    {
        if (keywordTokens.Length == 0 || songs.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var keywordSet = new HashSet<string>(keywordTokens, StringComparer.OrdinalIgnoreCase);
        var results = new List<(BaseItem Item, double Score)>();

        foreach (var song in songs)
        {
            string title = song.Name ?? string.Empty;
            string[] titleTokens = Tokenize(title, locale);

            if (titleTokens.Length == 0)
            {
                continue;
            }

            // Check keyword coverage: all user keywords must appear in title tokens
            int keywordsFound = 0;
            foreach (string keyword in keywordTokens)
            {
                if (Array.IndexOf(titleTokens, keyword) >= 0)
                {
                    keywordsFound++;
                }
            }

            double keywordCoverage = (double)keywordsFound / keywordTokens.Length;

            // All keywords must be found
            if (keywordCoverage < 1.0)
            {
                continue;
            }

            // Title coverage: how many title tokens are covered by user keywords
            int titleTokensCovered = 0;
            foreach (string titleToken in titleTokens)
            {
                if (keywordSet.Contains(titleToken))
                {
                    titleTokensCovered++;
                }
            }

            double titleCoverage = (double)titleTokensCovered / titleTokens.Length;

            double score = ((KeywordCoverageWeight * keywordCoverage) + (TitleCoverageWeight * titleCoverage)) * 100.0;

            // Positional bonus: first title token must be one of the user keywords
            if (titleTokens.Length > 0 && keywordSet.Contains(titleTokens[0]))
            {
                score += PositionalBonus;
            }

            results.Add((song, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// Extracts the locale prefix from a full locale string.
    /// "en-US" -> "en", "it-IT" -> "it", etc.
    /// </summary>
    private static string GetLocalePrefix(string locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return string.Empty;
        }

        int dashIndex = locale.IndexOf('-');
        return dashIndex > 0 ? locale.Substring(0, dashIndex) : locale;
    }

    /// <summary>
    /// Scores candidate songs against user keyword tokens using Double Metaphone
    /// phonetic matching. This is the phonetic counterpart to <see cref="Score"/> —
    /// it relaxes keyword coverage from 100% to 50% and applies a penalty multiplier
    /// so phonetic matches rank below exact matches.
    /// Designed for non-native speakers who misspell titles (e.g. "rapsodi" for "rhapsody").
    /// </summary>
    /// <param name="songs">Candidate songs (already filtered by phonetic index lookup).</param>
    /// <param name="keywordTokens">Pre-tokenized user keywords.</param>
    /// <param name="locale">The locale string for tokenizing song titles.</param>
    /// <returns>List of (Item, Score) tuples sorted by score descending, with phonetic penalty applied.</returns>
    public static List<(BaseItem Item, double Score)> ScorePhonetic(
        IReadOnlyList<BaseItem> songs, string[] keywordTokens, string locale)
    {
        if (keywordTokens.Length == 0 || songs.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        // Pre-compute phonetic codes for keyword tokens once
        var keywordPhonetics = new (string Primary, string? Alternate)[keywordTokens.Length];
        for (int i = 0; i < keywordTokens.Length; i++)
        {
            keywordPhonetics[i] = DoubleMetaphone.Encode(keywordTokens[i]);
        }

        var results = new List<(BaseItem Item, double Score)>();

        foreach (var song in songs)
        {
            string title = song.Name ?? string.Empty;
            string[] titleTokens = Tokenize(title, locale);

            if (titleTokens.Length == 0)
            {
                continue;
            }

            // Compute phonetic codes for title tokens
            var titlePhonetics = new (string Primary, string? Alternate)[titleTokens.Length];
            for (int i = 0; i < titleTokens.Length; i++)
            {
                titlePhonetics[i] = DoubleMetaphone.Encode(titleTokens[i]);
            }

            // Phonetic keyword coverage: how many user keywords phonetically match any title token
            int keywordsFound = 0;
            foreach (var (kwPrimary, kwAlternate) in keywordPhonetics)
            {
                if (string.IsNullOrEmpty(kwPrimary))
                {
                    continue;
                }

                bool found = false;
                foreach (var (ttPrimary, ttAlternate) in titlePhonetics)
                {
                    if (FuzzyMatcher.PhoneticCodesMatch(kwPrimary, kwAlternate, ttPrimary, ttAlternate))
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    keywordsFound++;
                }
            }

            double keywordCoverage = (double)keywordsFound / keywordTokens.Length;

            // Relaxed coverage threshold for phonetic matching
            if (keywordCoverage < MinPhoneticKeywordCoverage)
            {
                continue;
            }

            // Title coverage: how many title tokens are phonetically covered by user keywords
            int titleTokensCovered = 0;
            foreach (var (ttPrimary, ttAlternate) in titlePhonetics)
            {
                if (string.IsNullOrEmpty(ttPrimary))
                {
                    continue;
                }

                foreach (var (kwPrimary, kwAlternate) in keywordPhonetics)
                {
                    if (FuzzyMatcher.PhoneticCodesMatch(kwPrimary, kwAlternate, ttPrimary, ttAlternate))
                    {
                        titleTokensCovered++;
                        break;
                    }
                }
            }

            double titleCoverage = titleTokens.Length > 0
                ? (double)titleTokensCovered / titleTokens.Length
                : 0;

            double score = ((KeywordCoverageWeight * keywordCoverage) + (TitleCoverageWeight * titleCoverage))
                * 100.0 * PhoneticPenalty;

            // Positional bonus: first title token phonetically matches any keyword
            if (titleTokens.Length > 0 && titlePhonetics.Length > 0)
            {
                var (firstPrimary, firstAlternate) = titlePhonetics[0];
                foreach (var (kwPrimary, kwAlternate) in keywordPhonetics)
                {
                    if (FuzzyMatcher.PhoneticCodesMatch(kwPrimary, kwAlternate, firstPrimary, firstAlternate))
                    {
                        score += PositionalBonus;
                        break;
                    }
                }
            }

            results.Add((song, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ToList();
    }
}
