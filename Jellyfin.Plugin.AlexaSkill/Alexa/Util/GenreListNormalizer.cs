#nullable enable
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE genre-list normalizer (live incident 2026-09-23: a Genres entry itself
/// carried embedded semicolons from library tagging, so speech joined them raw and
/// the TTS read the punctuation aloud; the same soup starves exact-match query
/// seeds in the radio and recommend paths). Splits each entry on ';', trims, drops
/// empties, dedupes case-insensitively (first wins), preserving order.
/// </summary>
internal static class GenreListNormalizer
{
    /// <summary>Normalizes raw genre entries for speech or exact-match seeds.</summary>
    /// <param name="genres">The raw genre entries from an item.</param>
    /// <returns>The cleaned, deduplicated genre names in original order.</returns>
    internal static string[] Normalize(IEnumerable<string>? genres)
    {
        if (genres == null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string raw in genres)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string genre = part.Trim();
                if (genre.Length > 0 && seen.Add(genre))
                {
                    result.Add(genre);
                }
            }
        }

        return result.ToArray();
    }
}
