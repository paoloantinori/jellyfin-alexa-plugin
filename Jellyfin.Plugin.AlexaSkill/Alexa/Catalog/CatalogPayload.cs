#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The full payload uploaded to SMAPI for a catalog version.
/// </summary>
public class CatalogPayload
{
    /// <summary>
    /// Gets or sets the list of catalog values.
    /// </summary>
    [JsonPropertyName("values")]
#pragma warning disable CA1002, CA2227 // List required for JSON serialization
    public List<CatalogValue> Values { get; set; } = [];
#pragma warning restore CA1002, CA2227

    /// <summary>
    /// Build a catalog payload from a collection of Jellyfin library items.
    /// Same-titled items are deduplicated: the key is
    /// <see cref="CatalogValueFactory.CanonicalNameKey"/> (the trimmed,
    /// truncated value, case-insensitive), and the FIRST occurrence wins, so
    /// the survivor keeps the real Jellyfin id of the first item in fetch
    /// order (the library-priority order). Without the dedup, a book held
    /// both as a single-file leaf and as a chaptered folder (JF-825's
    /// audiobook shape) uploaded as two values with different ids and entity
    /// resolution picked one arbitrarily.
    /// </summary>
    /// <param name="type">The catalog type (artist, album, series, audiobook).</param>
    /// <param name="items">Collection of (Id, Name) tuples from the library.</param>
    /// <param name="synonymGenerator">Function that generates phonetic synonyms for a name given a locale.</param>
    /// <param name="locale">The Alexa locale for phonetic synonym generation.</param>
    /// <returns>A populated <see cref="CatalogPayload"/>.</returns>
    public static CatalogPayload FromItems(
        CatalogType type,
        IEnumerable<(Guid Id, string Name)> items,
        Func<string, string, List<string>> synonymGenerator,
        string locale)
    {
        var payload = new CatalogPayload();
        var seenCanonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((Guid id, string name) in items)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!seenCanonicalNames.Add(CatalogValueFactory.CanonicalNameKey(name)))
            {
                continue;
            }

            List<string> synonyms = synonymGenerator(name, locale);
            payload.Values.Add(CatalogValueFactory.Create(type, id, name, synonyms));
        }

        return payload;
    }

    /// <summary>
    /// Enforce the catalog-value cap on the FINAL payload and return the
    /// dropped entries (empty when nothing was dropped, so the caller's
    /// truncation warning fires exactly when a drop happened). The drop is a
    /// tail cut because payload order IS priority order: library values in
    /// fetch order (the SortName-ascending query order, not a designed
    /// priority model) first, then the static seed titles, with the locale's
    /// generic word appended last by the seed merge. The cut therefore drops
    /// the generic word first, then seed titles, then the library tail in
    /// reverse fetch order; a seed can never drop while a library value beyond
    /// it survives (JF-825).
    /// </summary>
    /// <param name="maxValueCount">The maximum number of values to keep.</param>
    /// <returns>The dropped tail, in payload order.</returns>
    internal IReadOnlyList<CatalogValue> TruncateTo(int maxValueCount)
    {
        int dropCount = Values.Count - maxValueCount;
        if (dropCount <= 0)
        {
            return [];
        }

        List<CatalogValue> dropped = Values.GetRange(maxValueCount, dropCount);
        Values.RemoveRange(maxValueCount, dropCount);
        return dropped;
    }
}
