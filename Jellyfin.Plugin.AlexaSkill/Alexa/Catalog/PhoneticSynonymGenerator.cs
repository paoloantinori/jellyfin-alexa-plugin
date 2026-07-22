#nullable enable
using System;
using System.Collections.Generic;

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
}
