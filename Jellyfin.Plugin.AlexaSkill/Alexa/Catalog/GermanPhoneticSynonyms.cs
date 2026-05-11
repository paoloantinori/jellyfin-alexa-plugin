#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates German phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by German speakers.
/// </summary>
public static class GermanPhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 German phonetic variant strings for an English name.
    /// Returns an empty list for names that are already German origin or are too short.
    /// </summary>
    /// <param name="name">The artist or album name to generate variants for.</param>
    /// <returns>A list of German phonetic variant strings.</returns>
#pragma warning disable CA1002 // List<T> return type is intentional for caller convenience
    public static List<string> Generate(string name)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsGermanOrigin(trimmed))
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

        // Add German-article variant for band names ("die <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "die " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsGermanOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] germanEndings = { "stein", "mann", "berg", "burg", "feld", "wald", "rich", "hardt", "hoff", "baum", "thal" };

            foreach (string ending in germanEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string[] knownGerman = { "rammstein", "kraftwerk", "scorpions", "accept", "helloween", "scooter", "nena", "falco", "bap" };
            foreach (string known in knownGerman)
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

        w = ReplaceAll(w, "th", "s");
        w = ReplaceAll(w, "w", "v");
        w = ReplaceAll(w, "ck", "k");

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
