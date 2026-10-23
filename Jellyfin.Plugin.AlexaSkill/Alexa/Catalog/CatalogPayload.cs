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
            if (CatalogValueFactory.Create(type, id, name, synonyms) is { } catalogEntry)
            {
                payload.Values.Add(catalogEntry);
            }
        }

        return payload;
    }

    /// <summary>
    /// Enforce the catalog-value cap on the FINAL payload and return the
    /// dropped entries plus how many of them came from the reserved static
    /// tail (empty/zero when nothing was dropped, so the caller's truncation
    /// warning fires exactly when a drop happened). The policy reads the
    /// payload layout [library values in fetch order (the SortName-ascending
    /// query order)][reserved static tail: the seed titles, then the locale's
    /// generic word] that the seed merge appends;
    /// <paramref name="reservedTailCount"/> is that tail's size. When the
    /// payload fits entirely (Values.Count &lt;= the cap) nothing drops and
    /// nothing is reserved: byte-identical in, byte-identical out. On ANY
    /// over-cap cut (JF-879, the maintainer's "riserva sempre i seed"
    /// decision, generalizing JF-848's exact-saturation partition): the
    /// static tail is the baseline recall floor (the JF-823 generic word and
    /// the seed titles, whose loss collapsed bare one-shot utterances to
    /// NO_SELECTION), so it is reserved FIRST and kept whole while the
    /// library head is cut to (cap − reservedTailCount). This closes the
    /// JF-848 just-below-cap discontinuity: a library one value under the
    /// cap used to lose the whole seed tail via the JF-825 tail cut, the
    /// exact NO_SELECTION regime the reservation exists to prevent; now
    /// every cut keeps the floor and takes the library head instead (a
    /// 49999-value library with a 22-entry tail loses 22 library values,
    /// not the seeds). With no reserved tail (seedless types) the arm
    /// degenerates to the plain JF-825 tail cut, byte-identical. Should the
    /// reserved tail alone exceed the cap (test seams or a pathological
    /// seed block; production static counts are tens against a 50000 cap),
    /// the library drops whole and the tail's excess drops after it, so the
    /// cap still binds.
    /// </summary>
    /// <param name="maxValueCount">The maximum number of values to keep.</param>
    /// <param name="reservedTailCount">The number of FINAL entries that are
    /// the static seed block (0 for seedless types); clamped to
    /// [0, Values.Count].</param>
    /// <returns>The dropped entries in drop order (the library block past
    /// the reserved head first, then any excess static tail; the seam-only
    /// shapes are the summary's subject) and how many of the dropped
    /// entries were static seed entries.</returns>
    internal (IReadOnlyList<CatalogValue> Dropped, int DroppedReservedCount) TruncateTo(int maxValueCount, int reservedTailCount)
    {
        int reserved = Math.Clamp(reservedTailCount, 0, Values.Count);
        int libraryCount = Values.Count - reserved;

        // The reserve-on-any-cut partition (JF-879; the policy rationale,
        // including the seedless degeneration to the JF-825 tail cut, lives
        // in the summary above).
        if (Values.Count > maxValueCount)
        {
            int keepLibrary = Math.Max(0, maxValueCount - reserved);
            int libraryDrop = libraryCount - keepLibrary;
            List<CatalogValue> dropped = Values.GetRange(keepLibrary, libraryDrop);
            Values.RemoveRange(keepLibrary, libraryDrop);

            // Reserved tail alone over the cap: its excess drops too, so the
            // cap binds even in this seam-only shape.
            int excess = Values.Count - maxValueCount;
            if (excess > 0)
            {
                dropped.AddRange(Values.GetRange(maxValueCount, excess));
                Values.RemoveRange(maxValueCount, excess);
            }

            return (dropped, dropped.Count - libraryDrop);
        }

        // No cut: the payload fits entirely, byte-identical in and out.
        return ([], 0);
    }
}
