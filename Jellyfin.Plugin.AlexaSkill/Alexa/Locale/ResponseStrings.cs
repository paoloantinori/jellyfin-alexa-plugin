using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Locale;

/// <summary>
/// Provides localized response strings loaded from embedded JSON resource files.
/// </summary>
public static class ResponseStrings
{
    private const string DefaultLocale = "en-US";
    private const string ResourcePrefix = "Jellyfin.Plugin.AlexaSkill.Alexa.Locale.";
    private const string ResourceSuffix = ".json";

    private static readonly Dictionary<string, Dictionary<string, string>> _locales = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();
    private static bool _initialized;
    private static ILogger? _logger;

    /// <summary>
    /// Sets the logger instance for fallback diagnostics. Called once during plugin startup.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public static void SetLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a localized string for the given key and locale.
    /// Fallback chain: exact locale -> language root (e.g. "es" for "es-MX") -> en-US -> key itself.
    /// </summary>
    /// <param name="key">The string key to look up.</param>
    /// <param name="locale">The locale identifier (e.g. "en-US", "it-IT").</param>
    /// <returns>The localized string value.</returns>
    public static string Get(string key, string locale = DefaultLocale)
    {
        EnsureInitialized();

        // Step 1: Try exact locale match
        if (TryGetValue(key, locale, out string? value))
        {
            return value;
        }

        // Step 2: Try language root (e.g. "es" for "es-MX")
        string languageRoot = GetLanguageRoot(locale);
        if (!string.IsNullOrEmpty(languageRoot)
            && !string.Equals(languageRoot, locale, StringComparison.OrdinalIgnoreCase)
            && TryGetValue(key, languageRoot, out value))
        {
            _logger?.LogDebug("Locale fallback: key '{Key}' not found in '{Locale}', using language root '{LanguageRoot}'", key, locale, languageRoot);
            return value;
        }

        // Step 3: Try en-US as ultimate fallback
        if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase)
            && TryGetValue(key, DefaultLocale, out value))
        {
            _logger?.LogDebug("Locale fallback: key '{Key}' not found in '{Locale}' or language root, using en-US", key, locale);
            return value;
        }

        // Step 4: Key not found anywhere - return the key itself
        _logger?.LogWarning("Locale key '{Key}' not found for locale '{Locale}', en-US, or any language root", key, locale);
        return key;
    }

    /// <summary>
    /// Extracts the language root from a locale identifier.
    /// For example, "es-MX" returns "es", "en-US" returns "en".
    /// Returns empty string if the locale doesn't contain a hyphen.
    /// </summary>
    private static string GetLanguageRoot(string locale)
    {
        int separatorIndex = locale.IndexOf('-', StringComparison.Ordinal);
        return separatorIndex > 0 ? locale.Substring(0, separatorIndex) : string.Empty;
    }

    /// <summary>
    /// Gets a localized and formatted string for the given key, locale, and arguments.
    /// </summary>
    /// <param name="key">The string key to look up.</param>
    /// <param name="locale">The locale identifier (e.g. "en-US", "it-IT").</param>
    /// <param name="args">The format arguments.</param>
    /// <returns>The localized and formatted string value.</returns>
    public static string Get(string key, string locale, params object[] args)
    {
        string template = Get(key, locale);
        return string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private static bool TryGetValue(string key, string locale, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (_locales.TryGetValue(locale, out var dict))
        {
            return dict.TryGetValue(key, out value);
        }

        return false;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string localeName = resourceName.Substring(
                    ResourcePrefix.Length,
                    resourceName.Length - ResourcePrefix.Length - ResourceSuffix.Length);

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    continue;
                }

                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (dict != null)
                {
                    _locales[localeName] = dict;
                }
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// Resets the loaded locale data. For testing purposes only.
    /// </summary>
    internal static void Reset()
    {
        lock (_lock)
        {
            _locales.Clear();
            _initialized = false;
            _logger = null;
        }
    }

    /// <summary>
    /// Registers locale data directly. For testing purposes only.
    /// </summary>
    /// <param name="locale">The locale identifier.</param>
    /// <param name="data">The key-value string data for the locale.</param>
    internal static void RegisterLocale(string locale, Dictionary<string, string> data)
    {
        EnsureInitialized();
        _locales[locale] = data;
    }
}
