#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Generates Japanese (romaji approximation) phonetic variants for English artist/album names
/// so Alexa recognizes them when spoken by Japanese speakers.
/// Based on katakana-to-romaji back-transliteration patterns.
/// </summary>
public static class JapanesePhoneticSynonyms
{
    /// <summary>
    /// Generates up to 3 Japanese phonetic variant strings for an English name.
    /// Returns an empty list for names that are already Japanese origin or are too short.
    /// </summary>
    /// <param name="name">The artist or album name to generate variants for.</param>
    /// <returns>A list of Japanese phonetic variant strings.</returns>
#pragma warning disable CA1002 // List<T> return type is intentional for caller convenience
    public static List<string> Generate(string name)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
        {
            return new List<string>();
        }

        string trimmed = name.Trim();

        if (IsJapaneseOrigin(trimmed))
        {
            return new List<string>();
        }

        var results = new List<string>();

        // Strip "The" article but do NOT add article variant (Japanese has no articles)
        StartsWithTheArticle(trimmed, out string withoutThe);

        string phonetic = ApplyPhoneticTransforms(withoutThe);

        if (!string.Equals(phonetic, withoutThe, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(phonetic);
        }

        // Japanese has no articles, so no article variant needed.

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool IsJapaneseOrigin(string name)
    {
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            string lower = name.ToLowerInvariant();

            string[] japaneseEndings = { "moto", "hashi", "kawa", "yama", "tanaka" };

            foreach (string ending in japaneseEndings)
            {
                if (lower.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string[] knownJapanese = { "yoko", "kennichi", "utada" };
            foreach (string known in knownJapanese)
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

        // Order matters: longer patterns first to avoid partial matches
        w = ReplaceAll(w, "th", "s");
        w = ReplaceAll(w, "ph", "f");
        w = ReplaceAll(w, "ck", "k");
        w = ReplaceAll(w, "tion", "shon");
        w = TransformSpecialSyllables(w);
        w = TransformV(w);
        w = TransformLR(w);
        w = AppendFinalVowel(w);

        return w;
    }

    private static string TransformSpecialSyllables(string word)
    {
        // Japanese katakana patterns: si→shi, ti→chi, tu→tsu, hu→fu
        var sb = new StringBuilder(word.Length + 2);
        string lower = word.ToLowerInvariant();

        for (int i = 0; i < word.Length; i++)
        {
            bool isUpper = char.IsUpper(word[i]);

            if (i + 1 < word.Length)
            {
                char next = lower[i + 1];
                bool nextIsUpper = char.IsUpper(word[i + 1]);

                // si → shi
                if (lower[i] == 's' && next == 'i')
                {
                    sb.Append(isUpper ? 'S' : 's');
                    sb.Append(nextIsUpper ? 'H' : 'h');
                    sb.Append(nextIsUpper ? 'I' : 'i');
                    i++; // skip next char
                    continue;
                }

                // ti → chi
                if (lower[i] == 't' && next == 'i')
                {
                    sb.Append(isUpper ? 'C' : 'c');
                    sb.Append(nextIsUpper ? 'H' : 'h');
                    sb.Append(nextIsUpper ? 'I' : 'i');
                    i++;
                    continue;
                }

                // tu → tsu
                if (lower[i] == 't' && next == 'u')
                {
                    sb.Append(isUpper ? 'T' : 't');
                    sb.Append(nextIsUpper ? 'S' : 's');
                    sb.Append(nextIsUpper ? 'U' : 'u');
                    i++;
                    continue;
                }

                // hu → fu
                if (lower[i] == 'h' && next == 'u')
                {
                    sb.Append(isUpper ? 'F' : 'f');
                    sb.Append(nextIsUpper ? 'U' : 'u');
                    i++;
                    continue;
                }
            }

            sb.Append(word[i]);
        }

        return sb.ToString();
    }

    private static string TransformV(string word)
    {
        // Japanese often uses "b" for "v" (old-style), modern keeps "v"
        // We transform to "b" as the phonetic variant
        return ReplaceAll(word, "v", "b");
    }

    private static string TransformLR(string word)
    {
        // Japanese does not distinguish L and R; both map to the same sound
        // Speakers often say "r" for English "l"
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.ToLowerInvariant(chars[i]) == 'l')
            {
                chars[i] = char.IsUpper(chars[i]) ? 'R' : 'r';
            }
        }

        return new string(chars);
    }

    private static string AppendFinalVowel(string word)
    {
        // Japanese syllables end in a vowel or "n" — append "u" (or "o") to
        // word-final consonants except "n"
        if (word.Length == 0)
        {
            return word;
        }

        char last = char.ToLowerInvariant(word[^1]);
        bool isUpper = char.IsUpper(word[^1]);

        if (char.IsLetter(last) && !IsVowel(last) && last != 'n' && last != 's')
        {
            return word + (isUpper ? "U" : "u");
        }

        return word;
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
