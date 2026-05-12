using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Music;

/// <summary>
/// Provides a static, configurable mapping of genres to similar/related genres.
/// Used to expand small result sets when an exact genre match yields few items.
/// All lookups are case-insensitive.
/// </summary>
internal static class GenreSimilarityMap
{
    /// <summary>
    /// Minimum number of results below which genre expansion is triggered.
    /// </summary>
    public const int ExpansionThreshold = 5;

    /// <summary>
    /// Maximum number of total items after expansion (avoids unbounded growth).
    /// </summary>
    public const int MaxExpandedResults = 50;

    private static readonly Dictionary<string, string[]> _similarityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rock"] = new string[] { "alternative", "indie rock", "classic rock", "hard rock", "grunge" },
        ["jazz"] = new string[] { "smooth jazz", "blues", "swing", "fusion", "bebop" },
        ["electronic"] = new string[] { "techno", "house", "ambient", "dubstep", "trance", "edm" },
        ["pop"] = new string[] { "dance pop", "indie pop", "synth pop", "europop" },
        ["metal"] = new string[] { "heavy metal", "death metal", "thrash metal", "power metal", "black metal" },
        ["classical"] = new string[] { "orchestral", "chamber music", "baroque", "romantic", "symphony" },
        ["hip hop"] = new string[] { "rap", "r&b", "trap", "soul", "gangsta rap" },
        ["country"] = new string[] { "folk", "bluegrass", "americana", "country rock" },
        ["blues"] = new string[] { "rhythm and blues", "delta blues", "chicago blues", "jazz" },
        ["r&b"] = new string[] { "soul", "funk", "neo soul", "hip hop" },
        ["reggae"] = new string[] { "dub", "ska", "dancehall", "roots reggae" },
        ["punk"] = new string[] { "punk rock", "hardcore punk", "post punk", "ska punk" },
        ["folk"] = new string[] { "indie folk", "acoustic", "singer songwriter", "bluegrass" },
        ["latin"] = new string[] { "salsa", "bachata", "reggaeton", "bossa nova", "latin pop" },
        ["soul"] = new string[] { "funk", "r&b", "motown", "neo soul" },
        ["funk"] = new string[] { "soul", "r&b", "disco", "groove" },
        ["disco"] = new string[] { "funk", "dance", "synth pop", "house" },
        ["indie"] = new string[] { "indie rock", "indie pop", "alternative", "indie folk" },
        ["alternative"] = new string[] { "indie rock", "grunge", "post punk", "alternative rock" },
        ["dance"] = new string[] { "house", "techno", "edm", "trance" }
    };

    /// <summary>
    /// Gets the similar genres for the given genre name.
    /// Lookup is case-insensitive.
    /// </summary>
    /// <param name="genre">The genre to look up similar genres for.</param>
    /// <returns>An array of similar genre names, or empty if the genre is not in the map.</returns>
    public static string[] GetSimilarGenres(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return Array.Empty<string>();
        }

        return _similarityMap.TryGetValue(genre, out string[]? similar)
            ? similar
            : Array.Empty<string>();
    }

    /// <summary>
    /// Checks whether the given genre has similar genres defined in the map.
    /// </summary>
    /// <param name="genre">The genre to check.</param>
    /// <returns>True if similar genres exist, false otherwise.</returns>
    public static bool HasSimilarGenres(string genre)
    {
        return !string.IsNullOrWhiteSpace(genre) && _similarityMap.ContainsKey(genre);
    }
}
