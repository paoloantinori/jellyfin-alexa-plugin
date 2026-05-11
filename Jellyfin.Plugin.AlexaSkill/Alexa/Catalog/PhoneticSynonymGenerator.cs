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
            _ => new List<string>()
        };
    }

    private static string GetLocalePrefix(string locale)
    {
        int idx = locale.IndexOf('-', StringComparison.Ordinal);
        return idx > 0 ? locale[..idx] : locale;
    }
}
