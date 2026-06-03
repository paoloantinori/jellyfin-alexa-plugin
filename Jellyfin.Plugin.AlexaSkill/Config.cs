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
    /// Per-locale invocation name overrides. When a locale is present here,
    /// this value takes precedence over <see cref="InvocationName"/>.
    /// This prevents the global default from overwriting locale-specific
    /// invocation names already baked into the interaction model templates.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LocaleInvocationNames = new Dictionary<string, string>()
    {
        ["it-IT"] = "mia collezione",
    };

    /// <summary>
    /// Length of the CSRF token.
    /// </summary>
    public const int CsrfTokenLength = 1024;

    /// <summary>
    /// Expiration time of the CSRF token in minutes.
    /// </summary>
    public const int CsrfTokenExpirationMinutes = 10;

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