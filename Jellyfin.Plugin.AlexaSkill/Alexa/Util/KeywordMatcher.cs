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
    /// Maximum ranking bonus from the residual-keyword tiebreak (JF-388). Must exceed
    /// the PositionalBonus (+5) to override it when the residual closeness clearly favors
    /// one candidate (80/100 vs 20/100 in the live case), but small enough that it cannot
    /// bridge a coverage-tier gap: a 100%-coverage candidate has NO unmatched keywords
    /// (residual = 0) and its base (~63.75) stays safely above a 50%-coverage candidate's
    /// best case (~37.5 + 5 positional + 10 residual = 52.5).
    /// </summary>
    private const double ResidualTiebreakCap = 10.0;

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
            "un", "una", "in", "su", "per", "con", "da", "e", "o", "che",
            // JF-446: the partitive/genitive article forms. An elicit answer like
            // "dei Koop" or "di pink floyd" must tokenize down to the artist alone or
            // the cross-media word-count guard counts the article and dead-ends.
            // Verified against the two invariants before adding: (1) no canonical
            // OUTPUT of AbbreviationCanonicalForms is a stop word (street/road/
            // avenue/part/volume are untouched by these forms); (2) the song n-gram
            // index is built with en-US and keeps these forms as TITLE tokens, which
            // only widens the index lookup keys: a query bigram that skips a stripped
            // article misses the index bigram but SongNgramIndexService.Search falls
            // back to the single-token scan, and KeywordMatcher.Score re-tokenizes
            // the title with the request locale, so coverage stays symmetric (the
            // documented JF-384 residual asymmetry).
            "dei", "degli", "delle"
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
        },
        // JF-389: the four remaining language prefixes of the 17 locales. Common
        // grammatical function words, following the pattern of the sets above;
        // romaji/romanized forms cover slot text as captured by the NLU. Accepted
        // collision class: some entries are also rare title words ("die" under nl,
        // "la" under ar - the de/fr/es sets have carried the same class since before
        // JF-389); symmetric per-locale stripping plus the empty-token guards
        // (Score/ScorePhonetic/n-gram search all return empty on 0 tokens) degrade
        // a single-stop-word title to a clean not-found, never a wrong match.
        // Entries deliberately EXCLUDED: hi "ya" (the invocative particle is a common
        // title word in Hindi/Urdu music: "Ya Ali", "Yaara"), hi "hai" (finite verb,
        // not a function word), ja "no" (real word in English/Italian - same JF-383
        // ambiguity as the abbreviation map; guard test
        // Tokenize_JapaneseParticleNo_IsNotCanonicalized), ar "al" (the definite
        // article is a name prefix: "Al Green", "Al Jarreau", "Al Di Meola").
        // Script entries are belt-and-braces and only include forms WITHOUT combining
        // marks: the tokenizer splits on IsLetterOrDigit, and Devanagari matras are
        // Mark-category code points, so entries like "का" would fragment and never
        // match (verified at runtime, review 2026-08-29). Hindi script input in
        // general fragments into consonant-only tokens - a pre-existing tokenizer
        // limitation, documented here, not addressed by JF-389.
        ["nl"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "de", "het", "een", "en", "of", "van", "in", "op", "aan",
            "bij", "met", "te", "voor", "tot", "die"
        },
        ["ja"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wa", "ga", "wo", "ni", "de", "to", "mo", "ka", "kara", "made", "yori", "nado",
            "の", "は", "を", "に", "で", "と", "も", "が", "から", "まで"
        },
        ["hi"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ka", "ki", "ke", "ko", "se", "par", "pe", "mein", "aur",
            "पर", "और"
        },
        ["ar"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fi", "min", "ila", "ala", "ma", "wa", "la", "bi",
            "في", "من", "إلى", "على", "ما", "ولا"
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

            // JF-388 residual tiebreak: candidates that tie on phonetic coverage (both
            // matched 'street') are separated by the fuzzy closeness of their NON-matching
            // keyword/title-token pairs. The live case: query 'the cater street';
            // 'Decatur St.' (cater PartialRatio decatur = 80) must outrank 'St. Gregory'
            // (cater PartialRatio gregory = 20), which otherwise wins via the positional
            // bonus on its canonicalized 'St.' in first position. This is the discriminating
            // signal the reverted JF-337 attempt identified, applied here ONLY as a ranking
            // contribution (never as an admission gate), so it cannot create false-positive
            // matches: a garbage keyword contributes ~0 and does not promote its candidate.
            score += ResidualKeywordTiebreak(keywordTokens, titleTokens, keywordPhonetics, titlePhonetics);

            results.Add((song, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// Computes a small ranking bonus from the fuzzy closeness of the NON-matching
    /// keyword/title-token pairs (JF-388). For each keyword that did NOT phonetically
    /// match any title token, finds the best PartialRatio against the unmatched title
    /// tokens; returns the average, scaled to at most <see cref="ResidualTiebreakCap"/>.
    /// This separates candidates tied on phonetic coverage ('Decatur St.' vs
    /// 'St. Gregory' for 'the cater street') without acting as an admission gate:
    /// garbage keywords score ~0 and never promote a candidate on their own.
    /// </summary>
    /// <param name="keywordTokens">The raw keyword tokens (for fuzzy comparison).</param>
    /// <param name="titleTokens">The raw title tokens (for fuzzy comparison).</param>
    /// <param name="keywordPhonetics">Pre-computed keyword phonetic codes.</param>
    /// <param name="titlePhonetics">Pre-computed title phonetic codes.</param>
    /// <returns>A bonus in [0, <see cref="ResidualTiebreakCap"/>].</returns>
    private static double ResidualKeywordTiebreak(
        string[] keywordTokens,
        string[] titleTokens,
        (string Primary, string? Alternate)[] keywordPhonetics,
        (string Primary, string? Alternate)[] titlePhonetics)
    {
        if (keywordTokens.Length == 0 || titleTokens.Length == 0)
        {
            return 0;
        }

        // Identify which keywords and title tokens did NOT phonetically match
        var unmatchedKeywords = new List<int>();
        for (int k = 0; k < keywordTokens.Length; k++)
        {
            bool matched = false;
            for (int t = 0; t < titlePhonetics.Length; t++)
            {
                if (FuzzyMatcher.PhoneticCodesMatch(keywordPhonetics[k].Primary, keywordPhonetics[k].Alternate,
                        titlePhonetics[t].Primary, titlePhonetics[t].Alternate))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                unmatchedKeywords.Add(k);
            }
        }

        if (unmatchedKeywords.Count == 0)
        {
            return 0;
        }

        var unmatchedTitleTokens = new List<int>();
        for (int t = 0; t < titleTokens.Length; t++)
        {
            bool matched = false;
            for (int k = 0; k < keywordPhonetics.Length; k++)
            {
                if (FuzzyMatcher.PhoneticCodesMatch(keywordPhonetics[k].Primary, keywordPhonetics[k].Alternate,
                        titlePhonetics[t].Primary, titlePhonetics[t].Alternate))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                unmatchedTitleTokens.Add(t);
            }
        }

        if (unmatchedTitleTokens.Count == 0)
        {
            return 0;
        }

        // Best PartialRatio of each unmatched keyword against the unmatched title tokens
        double total = 0;
        foreach (int k in unmatchedKeywords)
        {
            int best = 0;
            foreach (int t in unmatchedTitleTokens)
            {
                int ratio = FuzzyMatcher.PartialRatio(keywordTokens[k], titleTokens[t]);
                if (ratio > best)
                {
                    best = ratio;
                }
            }

            total += best;
        }

        double average = total / unmatchedKeywords.Count;

        // Scale to [0, ResidualTiebreakCap]: 80/100 closeness -> ~0.8 * cap
        return (average / 100.0) * ResidualTiebreakCap;
    }
}
