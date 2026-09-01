#nullable enable
using System;
using System.Collections.Generic;
using Alexa.NET.Request.Type;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Bare stop/cancel words (language-neutral across the skill's locales), shared by every
/// handler that can find itself receiving a captured slot value while a Dialog.ElicitSlot
/// is open: in that regime Alexa captures the user's next utterance INTO the elicited slot
/// instead of routing it to AMAZON.Stop/CancelIntent, so handlers treat a bare cancel word
/// as an implicit cancel (live evidence 2026-08-28/29, FindSong and simulate-skill).
/// </summary>
internal static class CancelWords
{
    // Locale audit (JF-423, 2026-09-01): the only per-locale stop/cancel vocabulary in
    // this repo is the it-IT template's AMAZON.StopIntent samples
    // (InteractionModel/templates/it-IT.yaml, the JF-402 deliberate exception); all six
    // are covered below, plus the other Italian imperatives and the English set for the
    // 5 en-* locales. Every OTHER flow-reachable locale (ar-SA, de-DE, es-ES, es-MX,
    // es-US, fr-CA, fr-FR, hi-IN, ja-JP, nl-NL, pt-BR, 11 of 17) relies on Amazon's
    // built-in Stop/Cancel intents, whose per-locale phrasing ("stopp", "arrête",
    // "detén", "pare", ...) is NOT in the repo, so this list deliberately does not guess
    // it. Consequence in those locales: a bare localized cancel word captured during an
    // open elicit is searched as a title, matches nothing, and re-prompts; recoverable
    // (the session ends on silence or a "stop"/built-in cancel) but not a clean cancel.
    // Extending requires a vetted per-locale source, not translated guesses.
    private static readonly HashSet<string> Words = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "stop", "ferma", "fermare", "fermo", "ferma tutto", "ferma la musica", "ferma riproduzione", "ferma la riproduzione", "annulla", "annullare", "cancella", "basta", "stoppa", "arresta", "cancel",
    };

    /// <summary>
    /// Whether the captured slot text is exactly a stop/cancel word (trimmed, whole-value
    /// match only: a keyword phrase merely containing "stop" is a legitimate search, e.g.
    /// a song titled "Don't Stop Believin'").
    /// </summary>
    /// <param name="slotValue">The raw captured slot value.</param>
    /// <returns>True when the value is a bare cancel word.</returns>
    internal static bool IsCancelWord(string? slotValue)
        => !string.IsNullOrWhiteSpace(slotValue) && Words.Contains(slotValue.Trim());

    /// <summary>
    /// Whether the request's dialog is still in progress, meaning the utterance arrived
    /// THROUGH an open Dialog.ElicitSlot/Delegate rather than as a fresh full-utterance
    /// match. The elicitation-trap escape hatches (FindSong, PlaySong, PlayAlbum) gate on
    /// this so a legitimate search for a title that happens to be a cancel word still
    /// runs (JF-423; code-review 2026-08-29).
    /// </summary>
    /// <param name="request">The incoming intent request.</param>
    /// <returns>True when dialogState is IN_PROGRESS.</returns>
    internal static bool IsDialogInProgress(IntentRequest request)
        => string.Equals(request.DialogState, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether any slot of the incoming request carries a bare cancel word. The escape
    /// hatches must inspect EVERY slot, not just their primary one: a force-routed
    /// request can carry the captured word in any slot (e.g. "annulla" misrouted to a
    /// sibling intent lands in its musician slot), and searching that as an artist name
    /// reopens the elicitation-trap loop the hatches exist to close (JF-423).
    /// </summary>
    /// <param name="request">The incoming intent request.</param>
    /// <returns>True when any slot value is a bare cancel word.</returns>
    internal static bool AnySlotIsCancelWord(IntentRequest request)
    {
        if (request.Intent.Slots == null)
        {
            return false;
        }

        foreach (var slot in request.Intent.Slots.Values)
        {
            if (IsCancelWord(slot.Value))
            {
                return true;
            }
        }

        return false;
    }
}
