#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates Spanish phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by Spanish speakers.
/// </summary>
public static class SpanishPhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 Spanish phonetic variant strings for an English name.
    /// Returns an empty list for names that are already Spanish origin or are too short.
    /// </summary>
    public static List<string> Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsSpanishOrigin(trimmed))
        {
            return new List<string>();
        }

        var results = new List<string>();
        bool startsWithThe = StartsWithTheArticle(trimmed, out string withoutThe);

        string phonetic = ApplyPhoneticTransforms(withoutThe);

        if (!string.Equals(phonetic, withoutThe, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(phonetic);
        }

        // Add Spanish-article variant for band names ("los <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "los " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsSpanishOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] spanishEndings = { "uez", "anza", "eza", "ero", "era", "ito", "ita", "illo", "illa", "aco", "aca", "azo", "al", "dad" };

            foreach (string ending in spanishEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string[] knownSpanish = { "shakira", "enrique", "iglesias", "ricky", "martin", "luis", "miguel", "julio", "glasias", "bisbal", "san", "sergio", "malo" };
            foreach (string known in knownSpanish)
            {
                if (string.Equals(lower, known, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool StartsWithTheArticle(string name, out string withoutThe)
    {
        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
        {
            withoutThe = name.Substring(4);
            return true;
        }

        withoutThe = name;
        return false;
    }

    private static bool LooksLikeBandName(string name)
    {
        return name.Contains(' ', StringComparison.Ordinal) && !name.Contains(',', StringComparison.Ordinal);
    }

    private static string ApplyPhoneticTransforms(string name)
    {
        return TransformEachWord(name, TransformWord);
    }

    private static string TransformEachWord(string name, Func<string, string> transform)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(words.Length);
        foreach (string word in words)
        {
            result.Add(transform(word));
        }

        return string.Join(' ', result);
    }

    private static string TransformWord(string word)
    {
        if (word.Length <= 1)
        {
            return word;
        }

        string w = word;

        w = ReplaceAll(w, "th", "d");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = ReplaceAll(w, "tion", "sion");
        w = ReplaceAll(w, "sh", "ch");
        w = TransformW(w);

        // Silent "h" at word start
        if (w.Length > 1 && char.ToLowerInvariant(w[0]) == 'h')
        {
            char next = char.ToLowerInvariant(w[1]);
            if (next != 'h')
            {
                w = w.Substring(1);
            }
        }

        return w;
    }

    private static string TransformW(string word)
    {
        // Spanish speakers say "gu" before front vowels (e/i), "u" otherwise
        return ReplaceAll(word, "w", "u");
    }

    private static string ReplaceAll(string source, string find, string replace)
    {
        int idx = source.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            source = string.Concat(source.AsSpan(0, idx), replace, source.AsSpan(idx + find.Length));
            idx = source.IndexOf(find, idx + replace.Length, StringComparison.OrdinalIgnoreCase);
        }

        return source;
    }
}
