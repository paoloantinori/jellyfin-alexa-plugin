using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Parses Italian number words to integers. The it-IT interaction model types every
/// number slot (<c>duration_minutes</c>, <c>chapter_number</c>, <c>season_number</c>,
/// <c>episode_number</c>) as the custom <c>ItalianNumber</c> slot type, whose values
/// are word forms, and Alexa returns slot values verbatim, so a spoken "trenta"
/// arrives as text where AMAZON.NUMBER locales resolve the same speech to "30"
/// (JF-451). The table mirrors the ItalianNumber slot values exactly. Digit strings
/// parse too, so the same helper serves the 16 AMAZON.NUMBER locales.
/// </summary>
internal static class ItalianNumberWords
{
    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uno"] = 1,
        ["due"] = 2,
        ["tre"] = 3,
        ["quattro"] = 4,
        ["cinque"] = 5,
        ["sei"] = 6,
        ["sette"] = 7,
        ["otto"] = 8,
        ["nove"] = 9,
        ["dieci"] = 10,
        ["undici"] = 11,
        ["dodici"] = 12,
        ["tredici"] = 13,
        ["quattordici"] = 14,
        ["quindici"] = 15,
        ["venti"] = 20,
        ["trenta"] = 30,
        ["quaranta"] = 40,
        ["cinquanta"] = 50,
        ["sessanta"] = 60,
        ["novanta"] = 90,
        ["cento"] = 100,
    };

    /// <summary>
    /// Parse an Italian number word (or digit string) to its integer value.
    /// </summary>
    /// <param name="text">The slot value, e.g. "trenta" or "30".</param>
    /// <param name="value">The parsed number.</param>
    /// <returns>True when the text is a recognized number.</returns>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return Words.TryGetValue(text.Trim(), out value);
    }
}
