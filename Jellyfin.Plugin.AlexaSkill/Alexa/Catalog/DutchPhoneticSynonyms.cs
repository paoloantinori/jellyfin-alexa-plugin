#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates Dutch phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by Dutch speakers.
/// Dutch speakers are generally proficient at English, so fewer transforms are needed.
/// </summary>
public static class DutchPhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 Dutch phonetic variant strings for an English name.
    /// Returns an empty list for names that are already Dutch origin or are too short.
    /// </summary>
    /// <param name="name">The artist or album name to generate variants for.</param>
    /// <returns>A list of Dutch phonetic variant strings.</returns>
#pragma warning disable CA1002 // List<T> return type is intentional for caller convenience
    public static List<string> Generate(string name)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsDutchOrigin(trimmed))
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

        // Add Dutch-article variant for band names ("de <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "de " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsDutchOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] dutchEndings = { "stra", "dam", "berg", "huis", "man" };

            foreach (string ending in dutchEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string[] knownDutch = { "golden", "earring", "ven", "broek" };
            foreach (string known in knownDutch)
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

        w = ReplaceAll(w, "sh", "sj");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = ReplaceAll(w, "th", "t");

        return w;
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
