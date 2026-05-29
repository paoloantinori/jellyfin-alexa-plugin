#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates Brazilian Portuguese phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by Portuguese speakers.
/// </summary>
public static class PortuguesePhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 Portuguese phonetic variant strings for an English name.
    /// Returns an empty list for names that are already Portuguese origin or are too short.
    /// </summary>
    /// <param name="name">The artist or album name to generate variants for.</param>
    /// <returns>A list of Portuguese phonetic variant strings.</returns>
#pragma warning disable CA1002 // List<T> return type is intentional for caller convenience
    public static List<string> Generate(string name)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsPortugueseOrigin(trimmed))
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

        // Add Portuguese-article variant for band names ("os <phonetic>").
        if (startsWithThe || LooksLikeBandName(trimmed))
        {
            string articleVariant = "os " + phonetic;
            if (!results.Contains(articleVariant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(articleVariant);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsPortugueseOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] portugueseEndings = { "eira", "inho", "inha", "eira", "eiro", "udes" };

            foreach (string ending in portugueseEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // Names ending in -ão (common Portuguese ending, e.g., "João", "São")
            if (lower.EndsWith("ão", StringComparison.Ordinal) || lower.EndsWith("ao", StringComparison.Ordinal))
            {
                return true;
            }

            string[] knownPortuguese = { "anitta", "ivete", "sangalo" };
            foreach (string known in knownPortuguese)
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

        w = ReplaceAll(w, "tion", "sion");
        w = ReplaceAll(w, "sh", "ch");
        w = ReplaceAll(w, "th", "d");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = TransformW(w);

        // Silent "h" at word start (Portuguese drops initial h like French/Spanish)
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
        // Portuguese speakers say "u" for w before consonants/end, "v" between vowels
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.ToLowerInvariant(chars[i]) == 'w')
            {
                bool isUpper = char.IsUpper(chars[i]);
                bool prevIsVowel = i > 0 && IsVowel(chars[i - 1]);
                bool nextIsVowel = i + 1 < chars.Length && IsVowel(chars[i + 1]);

                // Between vowels → "v"; otherwise → "u"
                if (prevIsVowel && nextIsVowel)
                {
                    chars[i] = isUpper ? 'V' : 'v';
                }
                else
                {
                    chars[i] = isUpper ? 'U' : 'u';
                }
            }
        }

        return new string(chars);
    }

    private static bool IsVowel(char c)
    {
        char lower = char.ToLowerInvariant(c);
        return lower == 'a' || lower == 'e' || lower == 'i' || lower == 'o' || lower == 'u';
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
