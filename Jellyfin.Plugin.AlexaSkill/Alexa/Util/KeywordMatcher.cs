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
    /// Tokenizes the input text by lowercasing, splitting on whitespace and punctuation,
    /// and removing locale-specific stop words.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="locale">The locale string (e.g. "en-US") used to resolve stop words.</param>
    /// <returns>An array of non-stop-word tokens, lowercased. Empty array for null, empty, or stop-words-only input.</returns>
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
                    string token = text.Substring(start, i - start).ToLowerInvariant();
                    if (stopWordSet == null || !stopWordSet.Contains(token))
                    {
                        tokens.Add(token);
                    }

                    start = -1;
                }
            }
        }

        // Handle trailing token
        if (start >= 0)
        {
            string token = text[start..].ToLowerInvariant();
            if (stopWordSet == null || !stopWordSet.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens.ToArray();
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
