#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Dispatches phonetic synonym generation to locale-specific generators.
/// Each locale has different phonetic rules for adapting English names
/// so Alexa recognizes them when spoken by non-English speakers.
/// </summary>
public static class PhoneticSynonymGenerator
{
    /// <summary>
    /// Generates phonetic variant strings for a name, using the rules
    /// appropriate for the given locale.
    /// </summary>
    /// <param name="name">The artist or album name to generate synonyms for.</param>
    /// <param name="locale">The Alexa request locale (e.g. "it-IT", "de-DE").</param>
    /// <returns>A list of phonetic variant strings, or empty if no transformation is needed.</returns>
#pragma warning disable CA1002 // Collection return type is intentional for caller convenience
    public static List<string> GenerateSynonyms(string name, string locale)
#pragma warning restore CA1002
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return new List<string>();
        }

        string prefix = GetLocalePrefix(locale);

        return prefix switch
        {
            "it" => ItalianPhoneticSynonyms.Generate(name),
            "de" => GermanPhoneticSynonyms.Generate(name),
            "es" => SpanishPhoneticSynonyms.Generate(name),
            "fr" => FrenchPhoneticSynonyms.Generate(name),
            "pt" => PortuguesePhoneticSynonyms.Generate(name),
            "ja" => JapanesePhoneticSynonyms.Generate(name),
            "nl" => DutchPhoneticSynonyms.Generate(name),
            _ => new List<string>()
        };
    }

    private static string GetLocalePrefix(string locale)
    {
        int idx = locale.IndexOf('-', StringComparison.Ordinal);
        return idx > 0 ? locale[..idx] : locale;
    }

    // --- JF-362: pronunciation rules shared by the /ŋ/-absent Romance generators ---
    // Italian/Spanish/French/Portuguese L1 speakers all lack the velar nasal /ŋ/ as a
    // phoneme, so they realize English "-ing" as /in/ (a structural L1-transfer effect,
    // not the native-English "g-dropping" sociolinguistic variable). They also
    // monophthongize English /oʊ/ and may surface words like "soul" as "sol". These rules
    // live here once and are invoked by each of those four generators' TransformWord so
    // catalog behavior stays consistent across locales. German and Dutch are Germanic and
    // DO have /ŋ/ (Ding, singen, zingen) — they deliberately do NOT call this helper.
    // See claudedocs/research_jf362-italian-phonetic-synonyms_2026-07-22.md.

    /// <summary>
    /// Whole-word overrides for English words whose Romance-L1 pronunciation diverges from
    /// the spelling in ways the suffix/substring rules below cannot capture. Sparse and
    /// case-insensitive; extend per attested ASR capture, not speculatively.
    /// </summary>
    private static readonly Dictionary<string, string> RomanceWordOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soul"] = "sol"
    };

    /// <summary>
    /// If <paramref name="word"/> is an override RESULT (e.g. "Sol" from "soul"->"sol"),
    /// restore it to the original vowel spelling ("Soul") so a second coverage variant can
    /// be derived from it. ASR is inconsistent about the override vowel — it captured both
    /// "sol coffin" and "soul coffin" — so both spellings must be offered. If the word was
    /// not produced by an override, returns it UNCHANGED (so non-override transformed words
    /// like "Cofin" are kept, not reverted to the original "Coughing"). Leading-case of the
    /// original is preserved.
    /// </summary>
    internal static string RestoreOverrideVowel(string word)
    {
        foreach (var kv in RomanceWordOverrides)
        {
            if (string.Equals(word, kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                return PreserveCasing(word, kv.Key);
            }
        }

        return word;
    }

    /// <summary>
    /// Applies the /ŋ/-absent-Romance pronunciation rules to a single word, after the
    /// locale's own transforms. Two steps: (1) an explicit whole-word override if the word
    /// is in the map (returns immediately — the override is the full spoken form); (2) a
    /// terminal "-ing" -> "-in" suffix transform (the morphological -ing suffix only, so
    /// "England" is untouched). Called from the Italian/Spanish/French/Portuguese
    /// generators' TransformWord. NOT called from German/Dutch (they have /ŋ/).
    /// </summary>
    internal static string ApplyRomanceTailRules(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        if (RomanceWordOverrides.TryGetValue(word, out string? overridden))
        {
            return PreserveCasing(word, overridden);
        }

        // Terminal -ing suffix -> -in. Only the morphological suffix (word ends with "-ing"
        // and is longer than 3 chars, so bare "ing" is excluded — "England" is untouched
        // because the -ing there is not a suffix). Preserve the leading case of the matched
        // suffix: "Coughing" -> "Coughin", "coughing" -> "coughin".
        if (word.Length > 3 && word.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            string stem = word[..^3];
            bool suffixWasCapitalized = char.IsUpper(word[^3]);
            return stem + (suffixWasCapitalized ? "In" : "in");
        }

        return word;
    }

    /// <summary>
    /// Applies the override's lowercase form to the original word's leading-character casing
    /// ("Soul" -> "Sol", "soul" -> "sol"), so generated synonyms match the name's casing.
    /// </summary>
    private static string PreserveCasing(string original, string lowerOverride)
    {
        if (original.Length > 0 && char.IsUpper(original[0]) && lowerOverride.Length > 0)
        {
            return char.ToUpperInvariant(lowerOverride[0]) + lowerOverride[1..];
        }

        return lowerOverride;
    }

    // --- JF-362 (device capture): coverage variants ---
    // The catalog-slot goal is COVERAGE, not precision: emit enough plausible spoken-form
    // variants that whichever way Alexa's Italian-locale ASR transcribes a Romance
    // pronunciation, at least one synonym matches. Entity resolution needs only one hit, so
    // extra near-miss synonyms are harmless. The two observed ASR artifacts this covers:
    // (1) ASR maps an /f/+/in/ sound onto the Italian loanword "coffin" (double-f), even
    // though the speaker produced a single /f/; (2) the soul->sol override captures one
    // monophthongization but ASR may also keep "soul". So for a transformed name we emit
    // additional variants: double each single intervocalic consonant, and where an override
    // changed a word, also keep a doubled-consonant form of it. See
    // claudedocs/research_jf362-italian-gemination-doubling_2026-07-23.md.

    /// <summary>
    /// Returns additional coverage variants of a transformed name, by doubling each single
    /// intervocalic consonant letter. E.g. "Sol Cofin" -> ["Sol Coffin"] (the intervocalic
    /// 'f' doubled). Bounded to one doubled-consonant variant per name (not the full
    /// combinatorial explosion) — the goal is to offer the common ASR-doubling, not every
    /// permutation. Word boundaries are respected; the first letter of each word is never
    /// doubled. Case-insensitive duplicates collapse at the caller's Distinct.
    /// </summary>
    internal static List<string> GetRomanceConsonantVariants(string transformed)
    {
        var variants = new List<string>();
        if (string.IsNullOrEmpty(transformed))
        {
            return variants;
        }

        // Build a variant where each single intervocalic consonant is doubled.
        var chars = transformed.ToCharArray();
        var sb = new System.Text.StringBuilder(transformed.Length + 4);
        bool changed = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            sb.Append(c);
            if (IsDoubledCandidate(chars, i))
            {
                sb.Append(c);
                changed = true;
            }
        }

        if (changed)
        {
            variants.Add(sb.ToString());
        }

        return variants;
    }

    /// <summary>
    /// True when the char at <paramref name="i"/> is a single consonant (not already doubled,
    /// not preceded/followed by another consonant) sitting between two vowels — the position
    /// where Italian-locale ASR most often inserts a doubled consonant.
    /// </summary>
    private static bool IsDoubledCandidate(char[] chars, int i)
    {
        char c = char.ToLowerInvariant(chars[i]);
        if (IsVowel(c))
        {
            return false;
        }

        // Only plain Latin consonant letters (skip digraphs/diacritics).
        if (c < 'a' || c > 'z')
        {
            return false;
        }

        // Must be intervocalic: a vowel immediately before and after.
        bool prevVowel = i > 0 && IsVowel(char.ToLowerInvariant(chars[i - 1]));
        bool nextVowel = i + 1 < chars.Length && IsVowel(char.ToLowerInvariant(chars[i + 1]));
        if (!prevVowel || !nextVowel)
        {
            return false;
        }

        // Not already doubled (don't triple an existing double).
        bool alreadyDoubledNext = i + 1 < chars.Length && char.ToLowerInvariant(chars[i + 1]) == c;
        bool alreadyDoubledPrev = i > 0 && char.ToLowerInvariant(chars[i - 1]) == c;
        return !alreadyDoubledNext && !alreadyDoubledPrev;
    }

    /// <summary>
    /// Adds each variant not already present (case-insensitive) into
    /// <paramref name="results"/>: the one dedup-append idiom shared by the
    /// coverage-variant families.
    /// </summary>
    private static void AppendDistinct(List<string> results, IEnumerable<string> variants)
    {
        foreach (string variant in variants)
        {
            if (!results.Contains(variant, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(variant);
            }
        }
    }

    /// <summary>
    /// Adds each consonant-doubled coverage variant of <paramref name="source"/> into
    /// <paramref name="results"/>, skipping case-insensitive duplicates and empty input.
    /// This is the append-or-skip companion to <see cref="GetRomanceConsonantVariants"/>,
    /// shared by all four Romance generators so the dedup+guard logic lives once.
    /// </summary>
    internal static void AddConsonantVariants(List<string> results, string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        AppendDistinct(results, GetRomanceConsonantVariants(source));
    }

    private static bool IsVowel(char c) =>
        c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' || c == 'è' || c == 'é' || c == 'à' || c == 'ì' || c == 'ò' || c == 'ù';

    // --- JF-379: velar-stop (c/k/ck/q) substitution family ---
    // Live evidence (2026-07-25, it-IT Echo): "Koop" heard as BOTH "cup" and "coop".
    // Romance-L1 speakers render foreign /k/ unpredictably across c/k/ck/q, and the
    // "oo" vowel drifts to /o/ and /u/. Same coverage goal as the doubling rules:
    // emit the plausible ASR outputs; entity resolution needs one hit.
    // Wired into ItalianPhoneticSynonyms ONLY for now: the device evidence is it-IT,
    // and QuGlide's /kw/ spelling is Italian orthography (Spanish writes /kw/ as "cu"
    // and allows no bare q; Portuguese splits qu/c by vowel) - those locales need
    // localized glide rules before wiring. Pan-Romance rollout belongs to JF-379's
    // staged generative plan, not a mirrored call site.

    /// <summary>
    /// Returns velar-stop coverage variants of a name: for each word containing a
    /// velar stop (k, q, or a HARD c - one not followed by e/i/y, where c reads /s/),
    /// the consonant swaps (ck collapses to c and k; k/c/q interchange) plus the
    /// "oo" vowel drifts (oo to o, oo to u) composed with the c-form, plus the
    /// Italian "qu" (/kw/) glide form. "Koop" yields Coop, Cup, Cop, Qoop, Quop.
    /// Each variant is the WHOLE name with only that word changed ("Simon Back" ->
    /// "Simon Bac", never a bare "Bac"); names with no velar stop yield nothing.
    /// </summary>
    /// <param name="name">The transformed (or original) name.</param>
    /// <returns>Variants covering the Romance-L1 /k/ transcription drift.</returns>
    internal static List<string> GetVelarStopVariants(string name)
    {
        var variants = new List<string>();
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(VelarStopLetters) < 0)
        {
            return variants;
        }

        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int w = 0; w < words.Length; w++)
        {
            if (!WordHasVelarStop(words[w]))
            {
                continue;
            }

            string prefix = w == 0 ? string.Empty : string.Join(' ', words, 0, w) + " ";
            string suffix = w + 1 < words.Length ? " " + string.Join(' ', words, w + 1, words.Length - w - 1) : string.Empty;
            AppendDistinct(variants, VelarStopWordVariants(words[w]).Select(v => prefix + v + suffix));
        }

        return variants;
    }

    private static readonly char[] VelarStopLetters = { 'c', 'C', 'k', 'K', 'q', 'Q' };

    /// <summary>True when the word contains a velar-stop letter (see <see cref="IsVelarStopAt"/>).</summary>
    private static bool WordHasVelarStop(string word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (IsVelarStopAt(word, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the letter at <paramref name="i"/> is a velar-stop drift site: k or q, or a hard c (<see cref="IsHardCAt"/>).</summary>
    private static bool IsVelarStopAt(string word, int i)
    {
        char c = char.ToLowerInvariant(word[i]);
        return c == 'k' || c == 'q' || (c == 'c' && IsHardCAt(word, i));
    }

    /// <summary>
    /// True when the c at <paramref name="i"/> reads /k/: NOT before e/i/y (soft c, /s/)
    /// and NOT before h. The "ch" digraph is excluded both ways - Italian "ch" is the
    /// NATIVE /k/ spelling before front vowels ("che"/"chi", so Italian-origin names
    /// like "Bianchi" must not drift), and English "ch" reads /tʃ/ - either way it is
    /// not a foreign /k/ drift site.
    /// </summary>
    private static bool IsHardCAt(string word, int i)
        => i + 1 >= word.Length
           || !(char.ToLowerInvariant(word[i + 1]) is 'e' or 'i' or 'y' or 'h');

    /// <summary>
    /// The per-word variant set, ordered by ASR attestation: the c-form first (the
    /// "coop" capture), then its oo-drifts ("cup", "cop"), then the q-form with its
    /// /kw/ glide ("Qoop"/"Quop" - Italian spells /kw/ as "qu", so /k/ before a
    /// rounded vowel can land there), then the ck collapse. The k-form itself is
    /// the input word and is not re-emitted.
    /// </summary>
    private static IEnumerable<string> VelarStopWordVariants(string word)
    {
        string cForm = SwapVelarStops(word, 'c');
        if (!string.Equals(cForm, word, StringComparison.OrdinalIgnoreCase))
        {
            yield return cForm;
            yield return DriftOo(cForm, 'u');
            yield return DriftOo(cForm, 'o');
        }

        string qForm = SwapVelarStops(word, 'q');
        if (!string.Equals(qForm, word, StringComparison.OrdinalIgnoreCase))
        {
            yield return qForm;
            yield return QuGlide(qForm);
        }

        // ck collapses to a single stop: Back -> Bac / Bak.
        string lower = word.ToLowerInvariant();
        int ck = lower.IndexOf("ck", StringComparison.Ordinal);
        if (ck >= 0)
        {
            yield return word.Remove(ck + 1, 1);
            yield return word.Remove(ck, 1);
        }
    }

    /// <summary>Replaces every velar-stop letter (k, q, hard c, and both halves of ck) with <paramref name="target"/>.</summary>
    private static string SwapVelarStops(string word, char target)
    {
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = char.ToLowerInvariant(chars[i]);
            bool velar = IsVelarStopAt(word, i);
            if (velar)
            {
                chars[i] = char.IsUpper(chars[i]) ? char.ToUpperInvariant(target) : target;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// The Italian "qu" (/kw/) glide form of a q-form word: the vowel run after the
    /// q keeps only its first vowel, preceded by the u glide ("Qoop" -> "Quop").
    /// Returns the input unchanged when the q is not followed by a vowel (deduped
    /// away by the caller).
    /// </summary>
    private static string QuGlide(string qForm)
    {
        string lower = qForm.ToLowerInvariant();
        int q = lower.IndexOf('q');
        if (q < 0 || q + 1 >= qForm.Length || !IsVowel(lower[q + 1]))
        {
            return qForm;
        }

        int runEnd = q + 1;
        while (runEnd + 1 < qForm.Length && IsVowel(lower[runEnd + 1]))
        {
            runEnd++;
        }

        // A q already followed by "u" carries the glide already (Italian "qu"):
        // nothing to insert - "Qure"/"Quba" must not become "Quure"/"Quuba".
        if (lower[q + 1] == 'u')
        {
            return qForm;
        }

        // Keep the run's FIRST vowel after the inserted glide: "Qoop" -> "Quop",
        // "Qoughing" -> "Quoghing".
        return qForm[..(q + 1)] + 'u' + qForm[q + 1] + qForm[(runEnd + 1)..];
    }

    /// <summary>Replaces every "oo" cluster with the single given vowel (the cup/cop drift).</summary>
    private static string DriftOo(string word, char vowel)
    {
        var result = new char[word.Length];
        int writePos = 0;
        for (int i = 0; i < word.Length; i++)
        {
            if (i + 1 < word.Length
                && char.ToLowerInvariant(word[i]) == 'o'
                && char.ToLowerInvariant(word[i + 1]) == 'o')
            {
                result[writePos++] = char.IsUpper(word[i]) ? char.ToUpperInvariant(vowel) : vowel;
                i++;
            }
            else
            {
                result[writePos++] = word[i];
            }
        }

        return new string(result, 0, writePos);
    }

    /// <summary>
    /// The append-or-skip companion to <see cref="GetVelarStopVariants"/>, mirroring
    /// <see cref="AddConsonantVariants"/>: adds each variant not already present.
    /// </summary>
    internal static void AddVelarStopVariants(List<string> results, string source)
        => AppendDistinct(results, GetVelarStopVariants(source));
}
