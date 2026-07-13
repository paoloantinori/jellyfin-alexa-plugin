using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Configuration;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Fuzzy string matching utility for tolerating typos in voice queries.
/// Uses Levenshtein distance to score and rank candidate matches, with an
/// optional Double Metaphone phonetic pre-filter to promote candidates that
/// sound similar even when their spelling diverges.
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>
    /// Minimum similarity score (0-100) for a match to be considered valid.
    /// </summary>
    public const int DefaultThreshold = 60;

    /// <summary>
    /// Similarity score returned by <see cref="PartialRatio"/> when one string
    /// contains the other. Used as the boundary between "near-exact" and
    /// "fuzzy" matches in <c>HandleFuzzyMiss</c>.
    /// </summary>
    public const int ContainmentScore = 90;

    /// <summary>
    /// Minimum similarity score for a candidate to be offered as a suggestion
    /// when no confident match is found. Scores between this and DefaultThreshold
    /// trigger "Did you mean?" or auto-play behavior depending on config.
    /// </summary>
    public const int SuggestionThreshold = 40;

    /// <summary>
    /// Score bonus applied when phonetic codes match between query and candidate.
    /// This promotes candidates that sound similar even when Levenshtein alone
    /// would rank them lower. The bonus is capped so it cannot push a poor
    /// Levenshtein match above the threshold on its own.
    /// </summary>
    private const int PhoneticBonus = 15;

    /// <summary>
    /// Gets the fuzzy match threshold from the user, falling back to the compile-time constant.
    /// </summary>
    public static int GetDefaultThreshold(Entities.User? user) =>
        user?.FuzzyMatchThreshold ?? DefaultThreshold;

    /// <summary>
    /// Gets the fuzzy suggestion threshold from the user, falling back to the compile-time constant.
    /// </summary>
    public static int GetSuggestionThreshold(Entities.User? user) =>
        user?.FuzzySuggestionThreshold ?? SuggestionThreshold;

    /// <summary>
    /// Find the best matching item from a list of candidates using partial ratio scoring.
    /// </summary>
    /// <typeparam name="T">The type of candidate items.</typeparam>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidates">Candidate items to match against.</param>
    /// <param name="selector">Function to extract the comparable string from each candidate.</param>
    /// <param name="threshold">Minimum score (0-100) to accept a match.</param>
    /// <returns>The best matching item, or default if no match above threshold.</returns>
    public static T? FindBestMatch<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, int threshold = DefaultThreshold)
        where T : class
    {
        var result = FindBestMatchWithScore(query, candidates, selector);
        return result.HasValue && result.Value.Score >= threshold ? result.Value.Item : null;
    }

    /// <summary>
    /// Find the best matching item using phonetic pre-filtering combined with Levenshtein scoring.
    /// When a phonetic lookup is available, candidates whose Double Metaphone codes match the query
    /// receive a score bonus. This helps catch cross-language pronunciation matches that pure
    /// Levenshtein would miss (e.g. "smit" matching "Smith").
    /// </summary>
    /// <typeparam name="T">The type of candidate items.</typeparam>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidates">Candidate items to match against.</param>
    /// <param name="selector">Function to extract the comparable string from each candidate.</param>
    /// <param name="candidateIdSelector">Function to extract the unique ID from each candidate (for phonetic lookup).</param>
    /// <param name="phoneticLookup">Function that returns pre-computed phonetic codes for a candidate ID, or false if unavailable.</param>
    /// <param name="threshold">Minimum score (0-100) to accept a match.</param>
    /// <returns>The best matching item, or default if no match above threshold.</returns>
    public static T? FindBestMatch<T>(
        string query,
        IEnumerable<T> candidates,
        Func<T, string> selector,
        Func<T, Guid> candidateIdSelector,
        Func<Guid, (string Primary, string? Alternate)?> phoneticLookup,
        int threshold = DefaultThreshold)
        where T : class
    {
        var result = FindBestMatchWithScore(query, candidates, selector, candidateIdSelector, phoneticLookup);
        return result.HasValue && result.Value.Score >= threshold ? result.Value.Item : null;
    }

    /// <summary>
    /// Find the best matching item regardless of threshold, returning the item with its score.
    /// Returns null only when query is empty/whitespace or candidates are empty.
    /// </summary>
    /// <typeparam name="T">The type of candidate items.</typeparam>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidates">Candidate items to match against.</param>
    /// <param name="selector">Function to extract the comparable string from each candidate.</param>
    /// <returns>The best match with its score, or null.</returns>
    public static (T Item, int Score)? FindBestMatchWithScore<T>(string query, IEnumerable<T> candidates, Func<T, string> selector)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string normalizedQuery = Normalize(query);
        int maxLenDiff = Math.Max(normalizedQuery.Length * 2, 15);
        T? bestMatch = null;
        int bestScore = 0;

        foreach (T candidate in candidates)
        {
            string candidateText = Normalize(selector(candidate));

            if (Math.Abs(candidateText.Length - normalizedQuery.Length) > maxLenDiff)
            {
                continue;
            }

            int score = PartialRatio(normalizedQuery, candidateText);

            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;

                if (bestScore >= ContainmentScore)
                {
                    return (bestMatch, bestScore);
                }
            }
        }

        return bestMatch != null ? (bestMatch, bestScore) : null;
    }

    /// <summary>
    /// Find the best matching item with phonetic pre-filter, returning the item with its score.
    /// Encodes the query's phonetic code once, then applies a score bonus to candidates whose
    /// pre-computed phonetic codes match. Levenshtein remains the primary scoring signal.
    /// </summary>
    /// <typeparam name="T">The type of candidate items.</typeparam>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidates">Candidate items to match against.</param>
    /// <param name="selector">Function to extract the comparable string from each candidate.</param>
    /// <param name="candidateIdSelector">Function to extract the unique ID from each candidate.</param>
    /// <param name="phoneticLookup">Function that returns pre-computed phonetic codes for a candidate ID, or null if unavailable.</param>
    /// <returns>The best match with its score (including phonetic bonus), or null.</returns>
    public static (T Item, int Score)? FindBestMatchWithScore<T>(
        string query,
        IEnumerable<T> candidates,
        Func<T, string> selector,
        Func<T, Guid> candidateIdSelector,
        Func<Guid, (string Primary, string? Alternate)?> phoneticLookup)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string normalizedQuery = Normalize(query);
        int maxLenDiff = Math.Max(normalizedQuery.Length * 2, 15);

        // Encode the query's phonetic code once for all comparisons
        var queryPhonetic = DoubleMetaphone.Encode(query);

        T? bestMatch = null;
        int bestScore = 0;

        foreach (T candidate in candidates)
        {
            string candidateText = Normalize(selector(candidate));

            if (Math.Abs(candidateText.Length - normalizedQuery.Length) > maxLenDiff)
            {
                continue;
            }

            int score = PartialRatio(normalizedQuery, candidateText);

            // Apply phonetic bonus: if the candidate's pre-computed phonetic codes match
            // the query's phonetic codes, boost the score
            if (score < ContainmentScore)
            {
                Guid candidateId = candidateIdSelector(candidate);
                var candidatePhonetic = phoneticLookup(candidateId);
                if (candidatePhonetic.HasValue)
                {
                    if (PhoneticCodesMatch(queryPhonetic.Primary, queryPhonetic.Alternate,
                            candidatePhonetic.Value.Primary, candidatePhonetic.Value.Alternate))
                    {
                        score = Math.Min(score + PhoneticBonus, 100);
                    }
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;

                if (bestScore >= ContainmentScore)
                {
                    return (bestMatch, bestScore);
                }
            }
        }

        return bestMatch != null ? (bestMatch, bestScore) : null;
    }

    /// <summary>
    /// Check if two sets of Double Metaphone codes indicate a phonetic match.
    /// Matches if any combination of primary/alternate codes are equal.
    /// </summary>
    internal static bool PhoneticCodesMatch(
        string queryPrimary, string? queryAlternate,
        string candidatePrimary, string? candidateAlternate)
    {
        if (queryPrimary.Length == 0 || candidatePrimary.Length == 0)
        {
            return false;
        }

        // Primary matches primary
        if (CodesEqual(queryPrimary, candidatePrimary))
        {
            return true;
        }

        // Primary matches alternate
        if (candidateAlternate != null && CodesEqual(queryPrimary, candidateAlternate))
        {
            return true;
        }

        // Alternate matches primary
        if (queryAlternate != null && CodesEqual(queryAlternate, candidatePrimary))
        {
            return true;
        }

        // Alternate matches alternate
        if (queryAlternate != null && candidateAlternate != null && CodesEqual(queryAlternate, candidateAlternate))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compare two phonetic codes for equality.
    /// </summary>
    private static bool CodesEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Rank candidates by similarity to the query, returning all above threshold sorted by score descending.
    /// </summary>
    /// <typeparam name="T">The type of candidate items.</typeparam>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidates">Candidate items to rank.</param>
    /// <param name="selector">Function to extract the comparable string from each candidate.</param>
    /// <param name="threshold">Minimum score (0-100) to include.</param>
    /// <returns>Candidates above threshold, sorted by match score descending.</returns>
    public static List<T> RankMatches<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, int threshold = DefaultThreshold)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<T>();
        }

        string normalizedQuery = Normalize(query);

        // Prune length-mismatched candidates before the (expensive) PartialRatio call —
        // mirrors FindBestMatchWithScore. Pure pre-filter: doesn't affect ranking, prunes
        // only candidates too dissimilar in length to score well (mostly substring false
        // positives like a short query against a much-longer album name).
        int maxLenDiff = Math.Max(normalizedQuery.Length * 2, 15);

        var scored = new List<(T Item, int Score)>();
        foreach (T candidate in candidates)
        {
            string candidateText = Normalize(selector(candidate));
            if (Math.Abs(candidateText.Length - normalizedQuery.Length) > maxLenDiff)
            {
                continue;
            }

            int score = PartialRatio(normalizedQuery, candidateText);
            if (score >= threshold)
            {
                scored.Add((candidate, score));
            }

            if (score == 100)
            {
                return new List<T> { candidate };
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Calculate partial ratio score between two strings.
    /// Uses the best matching substring of the longer string.
    /// </summary>
    /// <param name="a">The first string to compare.</param>
    /// <param name="b">The second string to compare.</param>
    /// <returns>A similarity score from 0 to 100.</returns>
    internal static int PartialRatio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        // Exact match is always 100
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        // If one contains the other, high score
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
        {
            return ContainmentScore;
        }

        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length > b.Length ? a : b;

        int bestScore = 0;
        int windowSize = shorter.Length;

        for (int i = 0; i <= longer.Length - windowSize; i++)
        {
            string window = longer.Substring(i, windowSize);
            int distance = LevenshteinDistance(shorter, window);
            int maxLen = Math.Max(shorter.Length, window.Length);
            int score = maxLen > 0 ? ((maxLen - distance) * 100) / maxLen : 0;

            if (score > bestScore)
            {
                bestScore = score;
            }
        }

        return bestScore;
    }

    /// <summary>
    /// Calculate Levenshtein edit distance between two strings.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>The edit distance between the two strings.</returns>
    internal static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;

        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        // Two-row optimization: O(m) space instead of O(n*m).
        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];

        for (int j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static string Normalize(string input)
    {
        return input.Trim().ToLowerInvariant();
    }
}
