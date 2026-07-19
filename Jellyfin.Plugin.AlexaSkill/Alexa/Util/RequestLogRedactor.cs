using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Redacts secrets and PII from logged Alexa request bodies so that enabling debug logging
/// for triage does not write the LWA access token, apiAccessToken, or Amazon userId to logs.
/// </summary>
public static class RequestLogRedactor
{
    // Matches accessToken/apiAccessToken/consentToken/userId JSON fields and captures the
    // key+colon. The value pattern consumes JSON escape sequences so an escaped quote in a
    // value can't truncate the match and leak the tail.
    private static readonly Regex SensitiveFieldRegex = new(
        @"(""(?:accessToken|apiAccessToken|consentToken|userId)""\s*:\s*)""[^\\""]*(?:\\.[^\\""]*)*""",
        RegexOptions.Compiled);

    // Masks the api_key query parameter in a stream URL (the Jellyfin token credential).
    private static readonly Regex ApiKeyParamRegex = new(
        @"(api_key=)[^&]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Mask the LWA access token, apiAccessToken, consentToken, and Amazon userId in an Alexa
    /// request body.
    /// </summary>
    /// <param name="body">The raw Alexa request body JSON.</param>
    /// <returns>The body with sensitive fields' values replaced by [REDACTED].</returns>
    public static string Redact(string body) =>
        SensitiveFieldRegex.Replace(body, @"$1""[REDACTED]""");

    /// <summary>
    /// Mask the api_key query parameter in a stream URL so the Jellyfin token credential is
    /// not written to logs.
    /// </summary>
    /// <param name="url">A stream URL that may contain an api_key query parameter.</param>
    /// <returns>The URL with the api_key value replaced by [REDACTED].</returns>
    public static string RedactUrl(string url) =>
        ApiKeyParamRegex.Replace(url, "$1[REDACTED]");
}
