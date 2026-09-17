using System;
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

    // Masks the api_key/ApiKey and token query parameters in a stream URL (the Jellyfin
    // token credential and the signed item-scoped stream token, JF-309; ApiKey is the
    // capital no-underscore form the 12.0 auth change made the only accepted shape on
    // auth-gated routes, JF-575, pinned by test since JF-580).
    private static readonly Regex ApiKeyParamRegex = new(
        @"((?:api_key|ApiKey|token)=)[^&]*",
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
    /// Mask the api_key and token query parameters in a stream URL so the Jellyfin token
    /// credential and the signed stream token (JF-309) are not written to logs.
    /// </summary>
    /// <param name="url">A stream URL that may contain api_key or token query parameters.</param>
    /// <returns>The URL with the values replaced by [REDACTED].</returns>
    public static string RedactUrl(string url) =>
        ApiKeyParamRegex.Replace(url, "$1[REDACTED]");

    /// <summary>
    /// JF-580: redact a THIRD-PARTY stream URL (an IPTV/tuner provider path) for
    /// logging by dropping the entire query string: provider URLs embed their own
    /// credentials (token/user/pass params) that are not ours to log, and their
    /// parameter names are unbounded so a param-name denylist cannot keep up. Only
    /// the scheme, host, and path are kept.
    /// </summary>
    /// <param name="url">The remote provider URL.</param>
    /// <returns>The URL with any query string replaced by [REDACTED].</returns>
    public static string RedactRemoteUrl(string url)
    {
        // Basic-auth userinfo in the authority (user:pass@host) is a standard M3U/IPTV
        // convention and is a credential: strip it before anything else.
        int atStart = url.IndexOf('@');
        if (atStart >= 0)
        {
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0 && atStart > schemeEnd + 3 && url.LastIndexOf('/', atStart) <= schemeEnd + 2)
            {
                url = url[..(schemeEnd + 3)] + "[REDACTED]@" + url[(atStart + 1)..];
            }
        }

        int queryStart = url.IndexOf('?');
        return queryStart < 0 ? url : url[..queryStart] + "?[REDACTED]";
    }
}
