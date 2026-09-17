using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-583 episode_position slot semantics. The slot is a restricted custom type
/// (the JF-354 Mood architecture) on PlayNextEpisodeIntent whose two values
/// distinguish the phrasings: "next" keeps the Jellyfin NextUp core (watch
/// state, the historical behavior and the EMPTY-slot default), "latest" asks
/// for RECENCY (PremiereDate desc, never filtered by watch state). Both values
/// carry the shared English ids next/latest in every locale (the JF-468
/// one-key-space rule), so the entity-resolution branch is locale-independent;
/// the raw-value branch covers requests whose entity resolution did not match
/// (an older deployed model, ASR drift) via the localized word table below,
/// the same backing LocalizedMoodMap gives the Mood slot. Anything that is not
/// a confident "latest" resolves to NextUp: the failure mode degrades to the
/// pre-JF-583 behavior instead of guessing.
/// </summary>
internal static class EpisodePosition
{
    /// <summary>
    /// The entity-resolution id every locale's "latest" type value carries
    /// (the languageModel type entries in templates/&lt;locale&gt;.yaml are the
    /// authoritative source of the ids; this is the consuming half).
    /// </summary>
    public const string LatestId = "latest";

    private static readonly FrozenDictionary<string, FrozenSet<string>> LatestWords = new Dictionary<string, FrozenSet<string>>
    {
        // Language-prefix keyed: the it/es/fr/de/... families share vocabulary.
        // Each set carries that family's canonical "latest" value plus its main
        // synonyms (the same words the templates declare as type synonyms).
        ["it"] = new HashSet<string> { "l'ultimo", "ultimo", "l'ultima", "ultima", "il più recente", "più recente" }.ToFrozenSet(StringComparer.Ordinal),
        ["en"] = new HashSet<string> { "latest", "the latest", "newest", "the newest", "last", "the last", "most recent", "the most recent" }.ToFrozenSet(StringComparer.Ordinal),
        ["de"] = new HashSet<string> { "die neueste", "neueste", "die letzte", "letzte", "aktuellste", "die aktuellste" }.ToFrozenSet(StringComparer.Ordinal),
        ["es"] = new HashSet<string> { "el último", "último", "el más reciente", "más reciente" }.ToFrozenSet(StringComparer.Ordinal),
        ["fr"] = new HashSet<string> { "le dernier", "dernier", "le plus récent", "plus récent" }.ToFrozenSet(StringComparer.Ordinal),
        ["pt"] = new HashSet<string> { "o último", "último", "o mais recente", "mais recente", "o mais novo", "mais novo" }.ToFrozenSet(StringComparer.Ordinal),
        ["nl"] = new HashSet<string> { "de nieuwste", "nieuwste", "de laatste", "laatste", "de meest recente" }.ToFrozenSet(StringComparer.Ordinal),
        ["hi"] = new HashSet<string> { "नवीनतम", "सबसे नया", "नया", "आख़िरी" }.ToFrozenSet(StringComparer.Ordinal),
        ["ja"] = new HashSet<string> { "最新の", "最新", "一番新しい" }.ToFrozenSet(StringComparer.Ordinal),
        ["ar"] = new HashSet<string> { "الأحدث", "أحدث", "الجديدة", "الأخيرة" }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Whether the episode_position slot asks for the latest (recency) episode.
    /// Entity resolution decides when it matched (id first, canonical name as
    /// the no-id belt and braces); the localized raw-value table decides
    /// otherwise. A missing, empty, or unrecognized slot is NEVER latest.
    /// </summary>
    /// <param name="slot">The episode_position slot (null when absent).</param>
    /// <param name="locale">The request locale, for the raw-value word table.</param>
    /// <returns>True when the recency path must run.</returns>
    internal static bool IsLatest(Slot? slot, string locale)
    {
        if (slot is null || string.IsNullOrWhiteSpace(slot.Value))
        {
            return false;
        }

        if (slot.Resolution?.Authorities is { Length: > 0 } authorities)
        {
            foreach (var authority in authorities)
            {
                if (authority.Status?.Code != "ER_SUCCESS_MATCH" || authority.Values is not { Length: > 0 })
                {
                    continue;
                }

                ResolutionValue resolved = authority.Values[0].Value;
                if (resolved != null)
                {
                    return string.Equals(resolved.Id, LatestId, StringComparison.OrdinalIgnoreCase)
                        || IsLatestWord(resolved.Name, locale);
                }
            }
        }

        return IsLatestWord(slot.Value, locale);
    }

    private static bool IsLatestWord(string? word, string locale)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrEmpty(locale))
        {
            return false;
        }

        string normalized = Normalize(word);
        string prefix = locale.Contains('-', StringComparison.Ordinal) ? locale[..locale.IndexOf('-', StringComparison.Ordinal)] : locale;
        return LatestWords.TryGetValue(prefix, out FrozenSet<string>? words) && words.Contains(normalized);
    }

    private static string Normalize(string word)
        => word.Trim().ToLowerInvariant().Replace('’', '\'');
}
