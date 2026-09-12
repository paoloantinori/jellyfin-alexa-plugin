using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// The locale roster and per-locale model resource paths, derived from the embedded
/// interaction models exactly the way production discovers them
/// (Util.GetLocalInteractionModels). Tests iterate locales through this class so a
/// locale #18 lands everywhere at once instead of being silently skipped by a
/// forgotten hardcoded list.
/// </summary>
internal static class TestLocales
{
    public static IReadOnlyCollection<Tuple<string, string>> Models() =>
        Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels();

    public static IReadOnlyList<string> AllLocales() =>
        Models().Select(model => model.Item1).ToList();

    /// <summary>Single-argument MemberData rows, one per locale.</summary>
    public static IEnumerable<object[]> LocaleRows() =>
        AllLocales().Select(locale => new object[] { locale });

    /// <summary>MemberData rows of (locale, model resource path).</summary>
    public static IEnumerable<object[]> LocalesWithResourcePaths() =>
        Models().Select(model => new object[] { model.Item1, model.Item2 });

    /// <summary>The Alexa/Locale/*.json response-string resource locales.</summary>
    public static IReadOnlyList<string> ResponseStringLocales() =>
        typeof(Jellyfin.Plugin.AlexaSkill.Util).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith("Jellyfin.Plugin.AlexaSkill.Alexa.Locale.", StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name.Split('.')[^2])
            .ToList();

    /// <summary>
    /// Locales outside the en- family, with the OrdinalIgnoreCase comparer production
    /// uses for the same classification (ModelDeploymentManager).
    /// </summary>
    public static IEnumerable<string> NonEnglishLocales() =>
        AllLocales().Where(locale => !locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase));

    public static string ResourcePath(string locale) =>
        Models().SingleOrDefault(model => model.Item1 == locale)?.Item2
            ?? throw new InvalidOperationException($"No embedded interaction model for locale '{locale}'");
}
