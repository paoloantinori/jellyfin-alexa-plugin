#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates French phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by French speakers.
/// </summary>
public static class FrenchPhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 French phonetic variant strings for an English name.
    /// Returns an empty list for names that are already French origin or are too short.
    /// </summary>
    public static List<string> Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsFrenchOrigin(trimmed))
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

        // Add French-article variant for band names ("les <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "les " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsFrenchOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] frenchEndings = { "eau", "eux", "ard", "ier", "ière", "otte", "elle", "ault", "ois", "oise", "eur", "euse" };

            foreach (string ending in frenchEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string[] knownFrench = { "daft", "punk", "mylene", "farmer", "johnny", "celine", "indochine", "noir", "desir" };
            foreach (string known in knownFrench)
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

        w = ReplaceAll(w, "th", "z");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = ReplaceAll(w, "sh", "ch");
        w = TransformW(w);
        w = TransformEE(w);

        // French speakers drop initial "h" entirely
        if (w.Length > 1 && char.ToLowerInvariant(w[0]) == 'h')
        {
            w = w.Substring(1);
        }

        return w;
    }

    private static string TransformW(string word)
    {
        // French speakers say "ou" for w before vowels, "v" otherwise
        return ReplaceAll(word, "w", "v");
    }

    private static string TransformEE(string word)
    {
        // "ee" → "i" (e.g., "Deep" → "Dip")
        return ReplaceAll(word, "ee", "i");
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
