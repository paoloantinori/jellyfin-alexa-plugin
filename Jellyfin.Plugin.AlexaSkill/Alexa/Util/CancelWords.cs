#nullable enable
using System.Collections.Generic;

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
    private static readonly HashSet<string> Words = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "stop", "ferma", "fermare", "fermo", "ferma tutto", "annulla", "annullare", "cancella", "basta", "stoppa", "arresta", "cancel",
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
}
