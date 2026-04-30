using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

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

    /// <summary>
    /// Gets a localized string for the given key and locale.
    /// Falls back to en-US if the locale is not found, and returns the key itself
    /// if the key is not found in any locale.
    /// </summary>
    /// <param name="key">The string key to look up.</param>
    /// <param name="locale">The locale identifier (e.g. "en-US", "it-IT").</param>
    /// <returns>The localized string value.</returns>
    public static string Get(string key, string locale = DefaultLocale)
    {
        EnsureInitialized();

        if (TryGetValue(key, locale, out string? value))
        {
            return value;
        }

        // Fallback to default locale
        if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase)
            && TryGetValue(key, DefaultLocale, out value))
        {
            return value;
        }

        // Return the key itself as last resort
        return key;
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

    private static bool TryGetValue(string key, string locale, out string? value)
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
        }
    }
}
