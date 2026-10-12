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
    /// dropped entries plus how many of them came from the reserved static
    /// tail (empty/zero when nothing was dropped, so the caller's truncation
    /// warning fires exactly when a drop happened). The policy reads the
    /// payload layout [library values in fetch order (the SortName-ascending
    /// query order)][reserved static tail: the seed titles, then the locale's
    /// generic word] that the seed merge appends;
    /// <paramref name="reservedTailCount"/> is that tail's size. Below
    /// saturation (the library block alone stays under the cap) the cut is
    /// the JF-825 tail cut, byte-identical: the cut line lands past the
    /// library block, so the drop is static-only (the generic word first,
    /// then seed titles in reverse append order) and no library value ever
    /// drops while a static entry survives. The boundary is deliberately
    /// threshold-shaped (JF-848's trade): a library just under the cap keeps
    /// this seed-tail drop, a library that fills the cap keeps the whole
    /// floor. AT saturation (the library block alone reaches the
    /// cap; in production exactly at it, because the fetch Limit equals the
    /// cap) the JF-848 reservation flips the bottom of that order: the
    /// static tail is the baseline recall floor (the JF-823 generic word and
    /// the seed titles, whose loss collapsed bare one-shot utterances to
    /// NO_SELECTION), so it is reserved FIRST and kept whole while the
    /// library head is cut to (cap − reservedTailCount). The trade-off is
    /// accepted deliberately: a saturated library sees fewer of its own
    /// values (up to reservedTailCount of them, ~23 for audiobooks) so
    /// saturation never evicts the floor. Should the reserved tail alone
    /// exceed the cap (test seams or a pathological seed block; production
    /// static counts are tens against a 50000 cap), its excess drops after
    /// the library tail and the cap still binds.
    /// </summary>
    /// <param name="maxValueCount">The maximum number of values to keep.</param>
    /// <param name="reservedTailCount">The number of FINAL entries that are
    /// the static seed block (0 for seedless types); clamped to
    /// [0, Values.Count].</param>
    /// <returns>The dropped entries in drop order (library tail first, then
    /// any excess static tail) and how many of the dropped entries were
    /// static seed entries.</returns>
    internal (IReadOnlyList<CatalogValue> Dropped, int DroppedReservedCount) TruncateTo(int maxValueCount, int reservedTailCount)
    {
        int reserved = Math.Clamp(reservedTailCount, 0, Values.Count);
        int libraryCount = Values.Count - reserved;

        // The saturation partition (JF-848; the policy rationale lives in the
        // summary above). With no reserved tail this arm degenerates to the
        // tail cut below, byte-identical, so seedless saturated types are
        // untouched by the reservation.
        if (libraryCount >= maxValueCount)
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

        int dropCount = Values.Count - maxValueCount;
        if (dropCount <= 0)
        {
            return ([], 0);
        }

        List<CatalogValue> tailDropped = Values.GetRange(maxValueCount, dropCount);
        Values.RemoveRange(maxValueCount, dropCount);

        // Below saturation the cut line is past the library block, so every
        // dropped entry is static (dropCount < reserved always holds here).
        return (tailDropped, dropCount);
    }
}
