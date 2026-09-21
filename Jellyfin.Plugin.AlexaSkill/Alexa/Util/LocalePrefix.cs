namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE locale-prefix extraction (JF-610): "en-US" -> "en", "it-IT" -> "it".
/// A dashless locale returns ITSELF (never a candidate key, so lookups keyed by
/// prefix simply miss) and an empty input returns empty. This replaced four
/// private copies whose dashless fallbacks diverged (ResponseStrings returned
/// string.Empty, the others returned the locale; its caller's
/// !Equals(languageRoot, locale) guard makes the unified semantics identical).
/// </summary>
public static class LocalePrefix
{
    /// <summary>Gets the language prefix of a locale identifier.</summary>
    /// <param name="locale">The locale identifier (e.g. "en-US").</param>
    /// <returns>The prefix before the dash, the locale itself when dashless, empty for empty input.</returns>
    public static string Of(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return string.Empty;
        }

        int dashIndex = locale.IndexOf('-', System.StringComparison.Ordinal);
        return dashIndex > 0 ? locale[..dashIndex] : locale;
    }
}
