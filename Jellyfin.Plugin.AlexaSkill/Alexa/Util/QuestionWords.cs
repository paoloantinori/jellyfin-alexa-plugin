#nullable enable
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Locale-keyed question/help word TOKENS for slot values captured while a
/// Dialog.ElicitSlot is open (JF-474): the user's natural question about the available
/// options ("quali ci sono?", "what can I say?") is free text in the elicited slot, not
/// a station/genre word. Unlike <see cref="CancelWords"/> this vocabulary is not
/// probe-vetted against the deployed model, because nothing here depends on Amazon-side
/// routing: the predicate runs on text the skill ALREADY captured. The match is
/// per-TOKEN (the question arrives embedded in a phrase), not whole-value: the benign
/// failure mode of a false positive is the available-list answer plus a re-ask, and a
/// library entry whose name contains a bare question word ("what", "quali") is not a
/// realistic station or genre. Locales with no entry (ja-JP carries its own set here)
/// fall back to the English set.
/// </summary>
internal static class QuestionWords
{
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "which", "list", "options", "help", "available",
    };

    private static readonly HashSet<string> ItalianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "quali", "cosa", "elenco", "lista", "opzioni", "aiuto", "disponibili",
    };

    private static readonly HashSet<string> GermanWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "was", "welche", "liste", "optionen", "hilfe", "verfügbar",
    };

    private static readonly HashSet<string> FrenchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "quoi", "quel", "quelle", "lesquels", "lesquelles", "liste", "options", "aide", "disponibles",
    };

    private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "qué", "cual", "cuáles", "lista", "opciones", "ayuda", "disponibles",
    };

    private static readonly HashSet<string> PortugueseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "qual", "quais", "lista", "opções", "ajuda", "disponíveis",
    };

    private static readonly HashSet<string> DutchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "wat", "welke", "lijst", "opties", "help", "beschikbaar",
    };

    private static readonly HashSet<string> HindiWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "क्या", "कौन", "सूची", "विकल्प", "मदद",
    };

    private static readonly HashSet<string> ArabicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ماذا", "أي", "قائمة", "خيارات", "مساعدة",
    };

    private static readonly HashSet<string> JapaneseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "何", "どれ", "リスト", "一覧", "ヘルプ",
    };

    private static readonly Dictionary<string, HashSet<string>> WordsByLocale = new(StringComparer.OrdinalIgnoreCase)
    {
        // en-* variants share one set.
        ["en-US"] = EnglishWords,
        ["en-GB"] = EnglishWords,
        ["en-AU"] = EnglishWords,
        ["en-CA"] = EnglishWords,
        ["en-IN"] = EnglishWords,
        ["it-IT"] = ItalianWords,
        ["de-DE"] = GermanWords,
        ["fr-FR"] = FrenchWords,
        ["fr-CA"] = FrenchWords,
        ["es-ES"] = SpanishWords,
        ["es-MX"] = SpanishWords,
        ["es-US"] = SpanishWords,
        ["pt-BR"] = PortugueseWords,
        ["nl-NL"] = DutchWords,
        ["hi-IN"] = HindiWords,
        ["ar-SA"] = ArabicWords,
        ["ja-JP"] = JapaneseWords,
    };

    // Whitespace and common sentence punctuation separate the tokens of the captured
    // phrase (ASR text rarely carries punctuation, but a trailing "?" must not glue
    // itself onto the last word). The apostrophe splits English contractions
    // ("what's" -> "what" + "s") so they still match.
    private static readonly char[] Separators = { ' ', '\t', '\n', '\r', '?', '!', '.', ',', ';', ':', '¿', '¡', '\'' };

    /// <summary>
    /// Whether the captured slot text is a question-shaped answer (its tokens include a
    /// question/help word for the request's locale).
    /// </summary>
    /// <param name="slotValue">The raw captured slot value.</param>
    /// <param name="locale">The request locale (e.g. "it-IT").</param>
    /// <returns>True when the value reads as a question about the options.</returns>
    internal static bool IsQuestion(string? slotValue, string locale)
    {
        if (string.IsNullOrWhiteSpace(slotValue))
        {
            return false;
        }

        HashSet<string> words = WordsByLocale.TryGetValue(locale, out HashSet<string>? localeWords) ? localeWords : EnglishWords;

        foreach (string token in slotValue.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Contains(token))
            {
                return true;
            }
        }

        return false;
    }
}
