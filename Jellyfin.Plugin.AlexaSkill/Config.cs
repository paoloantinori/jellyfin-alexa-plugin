using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill;

/// <summary>
/// Global config values for the plugin.
/// </summary>
public static class Config
{
    /// <summary>
    /// The name of the Alexa skill.
    /// </summary>
    public const string SkillName = "Jellyfin";

    /// <summary>
    /// The default invocation name of the Alexa skill.
    /// Amazon requires at least two words (e.g. "jellyfin player").
    /// Each user can override this with their own invocation name.
    /// </summary>
    public const string InvocationName = "jellyfin player";

    /// <summary>
    /// Per-locale invocation name overrides. When a user has not set a custom
    /// invocation name (empty/null), the locale entry here takes precedence over
    /// <see cref="InvocationName"/>. This lets each locale fall back to its own
    /// natural-language default already baked into the interaction model templates.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LocaleInvocationNames = new Dictionary<string, string>()
    {
        ["it-IT"] = "mia collezione",
    };

    /// <summary>
    /// Resolves the effective invocation name for a locale given a user's
    /// per-user value. An empty/null <paramref name="userInvocationName"/> means
    /// "use locale defaults": the <see cref="LocaleInvocationNames"/> entry when
    /// present, otherwise the global <see cref="InvocationName"/>. A non-empty
    /// custom name applies to <strong>all</strong> locales (including it-IT).
    /// </summary>
    /// <param name="locale">The locale code (e.g. "it-IT", "en-US").</param>
    /// <param name="userInvocationName">The user's per-user invocation name, or empty/null for defaults.</param>
    /// <returns>The invocation name to bake into the locale's interaction model.</returns>
    public static string EffectiveInvocationName(string locale, string? userInvocationName)
    {
        if (!string.IsNullOrWhiteSpace(userInvocationName))
        {
            return userInvocationName;
        }

        return LocaleInvocationNames.TryGetValue(locale, out string? localeName)
            ? localeName
            : InvocationName;
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> is the stored form of the
    /// global default invocation name (<see cref="InvocationName"/>). Used only
    /// by the one-time config migration to detect users whose pre-JF-300 stored
    /// name should be cleared so they get locale defaults. Do NOT use this for
    /// runtime equality — only the migration compares against the literal default.
    /// </summary>
    public static bool IsStoredGlobalDefault(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && string.Equals(value, InvocationName, StringComparison.Ordinal);

    /// <summary>
    /// Length of the CSRF token.
    /// </summary>
    public const int CsrfTokenLength = 1024;

    /// <summary>
    /// Expiration time of the CSRF token in minutes.
    /// </summary>
    public const int CsrfTokenExpirationMinutes = 10;

    /// <summary>
    /// Time-to-live, in seconds, for the signed item-scoped stream token that gates the
    /// video-audio endpoints (JF-309). 10 hours covers the longest legitimate single session
    /// (a full audiobook) without refresh.
    /// </summary>
    public const int StreamTokenTtlSeconds = 10 * 60 * 60;

    /// <summary>
    /// The path for the LWA Authorization Code callback endpoint.
    /// </summary>
    public const string LwaCallbackPath = "alexaskill/lwa/callback";

    /// <summary>
    /// Length of the LWA authorization page token.
    /// </summary>
    public const int LwaAuthorizePageTokenLength = 6;

    /// <summary>
    /// Expiration time of the LWA authorization page token in minutes.
    /// </summary>
    public const int LwaAuthorizePageTokenExpirationMinutes = 30;

    /// <summary>
    /// Name of the database file.
    /// </summary>
    public const string DbFilePath = "alexa-skill-plugin.db";

    /// <summary>
    /// Valid redirect urls for the Alexa skill during account linking process.
    /// </summary>
    public static readonly string[] ValidRedirectUrls = new string[]
    {
        "https://alexa.amazon.co.jp/spa/skill/account-linking-status.html?vendorId=",
        "https://layla.amazon.com/spa/skill/account-linking-status.html?vendorId=",
        "https://pitangui.amazon.com/spa/skill/account-linking-status.html?vendorId=",
    };
}