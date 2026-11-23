#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// JF-946: the song-title preparation pipeline for the SongTitle catalog.
/// The Fable-reviewed rules (grounded on the 12,766-track household library):
/// strip the suffixes nobody speaks, drop the junk titles, dedup on the
/// STRIPPED form BEFORE the cap (a song with five phonetic variants is one
/// catalog entry), and order by play count then recency so growth past the
/// cap cuts the least-played titles instead of the end of the alphabet (the
/// SortName-ascending fetch order the sibling types use would lose exactly
/// the alphabetical tail). The catalog's purpose is exact-title anchoring
/// (the lab evidence: only a value matching the spoken title makes the
/// PlayLibrarySong carrier SELECT), so the prepared value IS the stripped
/// spoken form.
/// </summary>
internal static class SongTitlePreparation
{
    /// <summary>
    /// Strip the spoken-irrelevant suffixes from a track title: trailing
    /// parenthetical groups (the ONE shared stripper the n-gram index also
    /// uses, full-width parens included) and a trailing " - " segment
    /// ("Title - Remastered" to "Title"; the plan's blunt suffix rule, so a
    /// whole-title " - " shape strips too and the dedup accepts the rare
    /// collision). Mid-title parentheses and separators stay (they are part
    /// of what the user says).
    /// </summary>
    internal static string StripTitle(string name)
    {
        string current = SongIndexSearch.StripTrailingParentheticalGroups(name.Trim());

        int sep = current.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
        {
            current = current[..sep].Trim();
        }

        return current;
    }

    /// <summary>
    /// The junk-title rule: after stripping, a catalog value that is empty,
    /// pure digits (bare track numbers), or the "no title" class (the
    /// household library carries 37 tracks literally named "no title")
    /// anchors nothing and wastes a value slot.
    /// </summary>
    internal static bool IsJunkTitle(string stripped)
    {
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return true;
        }

        if (stripped.All(char.IsDigit))
        {
            return true;
        }

        switch (stripped.ToLowerInvariant())
        {
            case "no title":
            case "untitled":
            case "unknown":
            case "unknown title":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when the title carries at least one word of four or more letters:
    /// the per-locale phonetic synonym generators operate on such words, and
    /// on shorter titles they emit noise (single-letter and two-letter
    /// fragments every title shares), so songs without a qualifying word
    /// upload value-only (JF-946 plan decision).
    /// </summary>
    internal static bool HasQualifyingWord(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length >= 4);

    /// <summary>
    /// The song synonym policy for <see cref="CatalogPayload.FromItems"/>:
    /// VALUES-ONLY. The live 2026-11-23 finding: Amazon's per-model size cap
    /// (1.5MB) counts the catalog-backed EFFECTIVE size, and 9,458 titles
    /// with per-locale phonetic synonyms pushed it-IT to 2.0MB and de-DE to
    /// 1.6MB, so every catalog-wired build failed ExceededMaximumModelSize
    /// and the wiring could never land (the 15:31-'song=-'-static mystery).
    /// The catalog's selection job is EXACT-title anchoring; ASR drift after
    /// selection is the handler-side fuzzy chain's job, so dropping song
    /// synonyms trades a redundant defense for the headroom that keeps the
    /// FULL library under the cap. If phonetic song variants are ever
    /// reintroduced, they must ride a per-title variant budget sized against
    /// the 1.5MB effective-model limit (the HasQualifyingWord gate below is
    /// kept for that future and for the seed path's shape).
    /// </summary>
    public static List<string> SongSynonyms(string name, string locale) =>
        new List<string>();

    /// <summary>
    /// The full pipeline over the raw candidates: strip, junk-exclude, order
    /// by play count (descending) then recency (descending, tiebreak), dedup
    /// on the stripped form keeping the highest-ranked representative (the
    /// most-played instance of the title), then cap. Generic over the carried
    /// item payload so the pipeline stays testable without Jellyfin entities
    /// while the sync passes its BaseItems straight through. Returns the
    /// capped ordered pairs plus the distinct pre-cap count (the M of the
    /// "N of M synced" pair).
    /// </summary>
    /// <typeparam name="T">The carried item payload, threaded untouched to
    /// the output (BaseItem in the sync; any object in tests).</typeparam>
    internal static List<(T Item, string Name)> Prepare<T>(
        IEnumerable<(T Item, string Name, int PlayCount, DateTime DateCreated)> candidates,
        int cap,
        out int totalDistinct)
    {
        var ordered = candidates
            .Select(c => (c.Item, Name: StripTitle(c.Name), c.PlayCount, c.DateCreated))
            .Where(c => !IsJunkTitle(c.Name))
            .OrderByDescending(c => c.PlayCount)
            .ThenByDescending(c => c.DateCreated)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<(T Item, string Name)>(ordered.Count);
        foreach (var entry in ordered)
        {
            if (seen.Add(entry.Name))
            {
                distinct.Add((entry.Item, entry.Name));
            }
        }

        totalDistinct = distinct.Count;
        return cap >= 0 && distinct.Count > cap
            ? distinct.GetRange(0, cap)
            : distinct;
    }
}
