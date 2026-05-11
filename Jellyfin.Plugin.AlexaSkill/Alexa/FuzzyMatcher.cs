using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Fuzzy string matching utility for tolerating typos in voice queries.
/// Uses Levenshtein distance to score and rank candidate matches.
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>
    /// Minimum similarity score (0-100) for a match to be considered valid.
    /// </summary>
    public const int DefaultThreshold = 60;

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
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string normalizedQuery = Normalize(query);
        T? bestMatch = null;
        int bestScore = 0;

        foreach (T candidate in candidates)
        {
            string candidateText = Normalize(selector(candidate));
            int score = PartialRatio(normalizedQuery, candidateText);

            if (score > bestScore && score >= threshold)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

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

        return candidates
            .Select(c => (Item: c, Score: PartialRatio(normalizedQuery, Normalize(selector(c)))))
            .Where(x => x.Score >= threshold)
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
            return 90;
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

        int[][] d = new int[n + 1][];
        for (int i = 0; i <= n; i++)
        {
            d[i] = new int[m + 1];
        }

        for (int i = 0; i <= n; i++)
        {
            d[i][0] = i;
        }

        for (int j = 0; j <= m; j++)
        {
            d[0][j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i][j] = Math.Min(
                    Math.Min(d[i - 1][j] + 1, d[i][j - 1] + 1),
                    d[i - 1][j - 1] + cost);
            }
        }

        return d[n][m];
    }

    private static string Normalize(string input)
    {
        return input.Trim().ToLowerInvariant();
    }
}
