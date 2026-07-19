using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Redacts secrets and PII from logged Alexa request bodies so that enabling debug logging
/// for triage does not write the LWA access token, apiAccessToken, or Amazon userId to logs.
/// </summary>
public static class RequestLogRedactor
{
    // Matches "accessToken"/"apiAccessToken"/"userId" JSON fields and captures the key+colon,
    // so the value can be replaced with [REDACTED].
    private static readonly Regex SensitiveFieldRegex = new(
        @"(""(?:accessToken|apiAccessToken|userId)""\s*:\s*)""[^""]*""",
        RegexOptions.Compiled);

    /// <summary>
    /// Mask the LWA access token, apiAccessToken, and Amazon userId in an Alexa request body.
    /// </summary>
    /// <param name="body">The raw Alexa request body JSON.</param>
    /// <returns>The body with sensitive fields' values replaced by [REDACTED].</returns>
    public static string Redact(string body) =>
        SensitiveFieldRegex.Replace(body, @"$1""[REDACTED]""");
}
