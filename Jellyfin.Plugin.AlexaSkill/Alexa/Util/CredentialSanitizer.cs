using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Strips invisible Unicode characters from OAuth credentials.
/// Browser copy-paste from the Amazon developer portal can inject
/// zero-width spaces, BOMs, directional marks, etc. that cause
/// authorization failures.
/// </summary>
public static partial class CredentialSanitizer
{
    /// <summary>
    /// Matches any character outside printable ASCII (U+0020..U+007E).
    /// OAuth credentials are always printable ASCII, so anything else
    /// is invisible junk from browser copy-paste.
    /// </summary>
    [GeneratedRegex(@"[^\x20-\x7E]")]
    private static partial Regex NonPrintableAsciiRegex();

    /// <summary>
    /// Strip non-printable-ASCII characters and trim whitespace.
    /// Returns <see cref="string.Empty"/> for null/whitespace input.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return NonPrintableAsciiRegex().Replace(value.Trim(), string.Empty);
    }
}
