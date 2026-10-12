using System;
using System.Collections.Generic;
using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-869 seek-slot semantics for SkipForwardBackIntent. The SeekDirection and
/// SeekUnit slot types are restricted custom types (the EpisodePosition
/// architecture) whose values carry the shared English ids in every locale
/// that declares the types (en-US and it-IT today; the templates' type entries
/// are the authoritative source of the ids; this is the consuming half), so
/// the entity-resolution branch is locale-independent; the raw-value branch
/// covers requests whose entity resolution did not match (an older deployed
/// model, ASR drift) via the word sets below. The sets are the flat union of
/// the words the locales carrying the intent speak today (the en-US values
/// plus synonyms, the it-IT values): when a new locale family gains the
/// SkipForwardBack samples, its words join THESE sets (the one edit point),
/// the CancelWords JF-444 lesson.
/// </summary>
internal static class SeekVocabulary
{
    /// <summary>The entity-resolution id every locale's "back" direction value carries.</summary>
    public const string BackwardId = "back";

    /// <summary>The entity-resolution id every locale's "minutes" unit value carries.</summary>
    public const string MinutesId = "minutes";

    private static readonly HashSet<string> BackwardWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "back", "backward", "backwards", "indietro",
    };

    private static readonly HashSet<string> MinuteWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "minutes", "minute", "minuti", "minuto",
    };

    /// <summary>
    /// Whether the seek_direction slot asks to skip backward: entity resolution
    /// decides when it matched (id first, the resolved name as the no-id belt
    /// and braces); the raw spoken word decides otherwise. Anything else is
    /// forward (the handler default).
    /// </summary>
    /// <param name="slot">The seek_direction slot (null when absent).</param>
    /// <returns>True when the skip direction is backward.</returns>
    internal static bool IsBackward(Slot? slot) => Matches(slot, BackwardId, BackwardWords);

    /// <summary>
    /// Whether the seek_unit slot scales the amount by minutes instead of
    /// seconds: entity resolution first, the raw spoken word as the fallback.
    /// </summary>
    /// <param name="slot">The seek_unit slot (null when absent).</param>
    /// <returns>True when the unit is minutes.</returns>
    internal static bool IsMinutes(Slot? slot) => Matches(slot, MinutesId, MinuteWords);

    /// <summary>
    /// Resolves a restricted-vocabulary slot against its shared id and raw word
    /// set: entity resolution first (id, then the resolved name, via the ONE
    /// authority-walk owner <see cref="SlotValueHelper.GetResolvedValue"/>),
    /// the raw value as the no-ER fallback.
    /// </summary>
    /// <param name="slot">The slot to resolve.</param>
    /// <param name="erId">The entity-resolution id that means "true" for this slot.</param>
    /// <param name="rawWords">The spoken words that mean "true" without resolution.</param>
    /// <returns>True when the slot resolves to the given id or word set.</returns>
    private static bool Matches(Slot? slot, string erId, HashSet<string> rawWords)
    {
        if (slot is null || string.IsNullOrWhiteSpace(slot.Value))
        {
            return false;
        }

        if (SlotValueHelper.GetResolvedValue(slot) is { } resolved)
        {
            return string.Equals(resolved.Id, erId, StringComparison.OrdinalIgnoreCase)
                || (resolved.Name is not null && rawWords.Contains(resolved.Name));
        }

        return rawWords.Contains(slot.Value.Trim());
    }
}
