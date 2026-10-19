using System;
using System.Collections.Generic;
using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-869 seek-slot semantics for SkipForwardBackIntent. The SeekDirection and
/// SeekUnit slot types are restricted custom types (the EpisodePosition
/// architecture) whose values carry the shared English ids in every locale
/// that declares the types (all 17 since the JF-875 rollout; the templates'
/// type entries are the authoritative source of the ids; this is the consuming
/// half), so the entity-resolution branch is locale-independent; the raw-value
/// branch covers requests whose entity resolution did not match (an older
/// deployed model, ASR drift) via the word sets below. The sets are the flat
/// union of the direction/unit words every locale's types speak (values plus
/// synonyms, including the verb-direction synonyms like the en "rewind" and
/// the es "retrocede", the rewind-as-synonym mechanism): when a locale's
/// seek vocabulary changes, its words change HERE (the one edit point; the
/// SeekVocabularyTests mirror guard fails the suite on drift either way).
/// Unlike CancelWords (JF-444) the union is deliberately NOT locale-keyed:
/// CancelWords serves free-text slots where a foreign-locale word
/// false-positives against arbitrary user content, while these slots are
/// restricted types whose cross-locale collision surface is empty (the
/// negative sides of the mirror guard pin that standing) and the spoken
/// language can differ from the request locale (an it-IT user saying
/// "rewind", the JF-384 cross-locale class).
/// </summary>
internal static class SeekVocabulary
{
    /// <summary>The entity-resolution id every locale's "back" direction value carries.</summary>
    public const string BackwardId = "back";

    /// <summary>The entity-resolution id every locale's "minutes" unit value carries.</summary>
    public const string MinutesId = "minutes";

    private static readonly HashSet<string> BackwardWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // en family (rewind joined in JF-875, the rewind-as-synonym mechanism)
        "back", "backward", "backwards", "rewind",
        // it-IT
        "indietro",
        // de-DE
        "zurück", "rückwärts",
        // es family + pt-BR (atrás shared; "hacia atrás" joined in JF-877 as
        // the raw-branch twin of the type synonym, the probes' phrasing)
        "atrás", "retrocede", "hacia atrás",
        // fr family
        "en arrière", "recule",
        // hi-IN
        "पीछे",
        // ja-JP
        "戻して", "戻って",
        // nl-NL
        "terug", "achteruit",
        // pt-BR
        "para trás", "volta",
        // ar-SA
        "للخلف", "ارجع", "تراجع",
    };

    private static readonly HashSet<string> MinuteWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // en family + it-IT + fr family
        "minutes", "minute", "minuti", "minuto",
        // de-DE ("Minute" case-folds onto the en "minute" entry)
        "Minuten",
        // es family + pt-BR
        "minutos",
        // hi-IN
        "मिनट", "मिनटों", "मिनट्स",
        // ja-JP
        "分",
        // nl-NL ("minuten" case-folds onto the de-DE "Minuten" entry)
        "minuut",
        // ar-SA
        "دقيقة", "دقائق",
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
