#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates Italian phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by Italian speakers.
/// </summary>
public static class PhoneticSynonymGenerator
{
    /// <summary>
    /// Generates up to 3 Italian phonetic variant strings for an English name.
    /// Returns an empty list for names that are already Italian/Latin origin or are too short.
    /// </summary>
    /// <param name="name">The artist or album name to generate synonyms for.</param>
    /// <returns>A list of phonetic variant strings, or empty if no transformation is needed.</returns>
    public static List<string> GenerateSynonyms(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsItalianOrigin(trimmed))
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

        // Try a secondary variant with slightly different handling for broader coverage.
        string phoneticAlt = ApplyPhoneticTransformsAlternate(withoutThe);
        if (!string.Equals(phoneticAlt, phonetic, StringComparison.Ordinal)
            && !string.Equals(phoneticAlt, withoutThe, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(phoneticAlt);
        }

        // Add Italian-article variant for band names ("i <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "i " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsItalianOrigin(string name)
    {
        // Single-word names with common Italian/Latin endings are considered Italian origin.
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] italianEndings = { "ello", "etta", "etti", "ella", "allo", "alli", "alla", "uzzi", "otti", "ucci", "iano", "iani" };

            foreach (string ending in italianEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // Longer single words ending in -o, -a, -i, -e with doubled consonants are likely Italian.
            if (lower.Length > 4)
            {
                char last = lower[^1];
                if ((last == 'o' || last == 'a' || last == 'i' || last == 'e') && HasDoubledConsonant(lower))
                {
                    return true;
                }
            }

            // Known Italian-origin names.
            string[] knownItalian = { "metallica", "adele", "laura", "pausini", "eros", "ramazzotti", "vasco", "rossi", "luciano", "pavarotti", "andrea", "bocelli" };
            foreach (string known in knownItalian)
            {
                if (string.Equals(lower, known, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasDoubledConsonant(string lower)
    {
        for (int i = 1; i < lower.Length; i++)
        {
            char prev = lower[i - 1];
            char curr = lower[i];
            if (prev == curr && char.IsLetter(prev))
            {
                return true;
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
        // Multi-word names with no comma are typically band names.
        return name.Contains(' ', StringComparison.Ordinal) && !name.Contains(',', StringComparison.Ordinal);
    }

    private static string ApplyPhoneticTransforms(string name)
    {
        return TransformEachWord(name, w => TransformWord(w, "sion", w => TransformWByVowel(w), collapseDoubles: false));
    }

    private static string ApplyPhoneticTransformsAlternate(string name)
    {
        return TransformEachWord(name, w => TransformWord(w, "zion", TransformWAlwaysV, collapseDoubles: true));
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

    private static string TransformWord(string word, string tionReplacement, Func<string, string> transformW, bool collapseDoubles)
    {
        if (word.Length <= 1)
        {
            return word;
        }

        string w = word;

        w = ReplaceAll(w, "ough", "of");
        w = ReplaceAll(w, "tion", tionReplacement);
        w = ReplaceAll(w, "sh", "sc");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = ReplaceAll(w, "th", "t");
        w = transformW(w);

        // Silent "h" at word start
        if (w.Length > 1 && char.ToLowerInvariant(w[0]) == 'h')
        {
            char next = char.ToLowerInvariant(w[1]);
            if (next != 'h')
            {
                w = w.Substring(1);
            }
        }

        if (collapseDoubles)
        {
            w = CollapseDoubledConsonants(w);
        }

        return w;
    }

    private static string TransformWByVowel(string word)
    {
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.ToLowerInvariant(chars[i]) == 'w')
            {
                bool isUpper = char.IsUpper(chars[i]);
                bool nextIsFrontVowel = i + 1 < chars.Length && IsFrontVowel(chars[i + 1]);

                chars[i] = nextIsFrontVowel
                    ? (isUpper ? 'V' : 'v')
                    : (isUpper ? 'U' : 'u');
            }
        }

        return new string(chars);
    }

    private static string TransformWAlwaysV(string word)
    {
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.ToLowerInvariant(chars[i]) == 'w')
            {
                chars[i] = char.IsUpper(chars[i]) ? 'V' : 'v';
            }
        }

        return new string(chars);
    }

    private static bool IsFrontVowel(char c)
    {
        char lower = char.ToLowerInvariant(c);
        return lower == 'e' || lower == 'i';
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

    private static string CollapseDoubledConsonants(string word)
    {
        if (word.Length <= 1)
        {
            return word;
        }

        var result = new char[word.Length];
        int writePos = 0;

        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0 && char.ToLowerInvariant(word[i]) == char.ToLowerInvariant(word[i - 1]) && char.IsLetter(word[i]))
            {
                continue;
            }

            result[writePos++] = word[i];
        }

        return new string(result, 0, writePos);
    }
}
