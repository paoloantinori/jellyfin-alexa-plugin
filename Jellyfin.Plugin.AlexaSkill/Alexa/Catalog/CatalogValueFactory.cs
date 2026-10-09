#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The ONE construction path for <see cref="CatalogValue"/> catalog entries
/// (JF-689): id formatting, the 140-char slot truncation on the value and every
/// synonym, the null-vs-empty synonym normalization, and the JF-684 partial-name
/// enrichment all live here. Both builders (<see cref="CatalogPayload.FromItems"/>
/// for library items, <see cref="CatalogSeedEnrichment"/> for the static seeds)
/// route through it, so a future Artist-catalog construction site cannot ship
/// without the partial synonym (the silent on-device selection-gate regression
/// JF-684 diagnosed); the assembly-scan pin in CatalogValueFactoryTests fails on
/// any CatalogValue construction outside this factory.
/// </summary>
internal static class CatalogValueFactory
{
    /// <summary>
    /// The ONE canonical-name key the payload builders dedup on (JF-825): the
    /// value the entry would carry (the 140-char truncation) TRIMMED, so
    /// 'Sapiens' from a single-file leaf and 'Sapiens ' from scraped metadata
    /// collapse to one catalog value, and the upload side's notion of the same
    /// name agrees with the ER read side (SlotValueHelper.GetCanonicalValues
    /// already dedups authority names by Trim). Case-insensitivity lives in
    /// the callers' set comparer, which stays OrdinalIgnoreCase. Shared by
    /// CatalogPayload.FromItems (library-vs-library) and
    /// CatalogSeedEnrichment.MergeSeeds (seed-vs-seed and seed-vs-library), so
    /// the key cannot drift between the two dedup sites.
    /// </summary>
    /// <param name="name">The raw, untruncated display name.</param>
    /// <returns>The trimmed canonical key for set membership.</returns>
    internal static string CanonicalNameKey(string name) => SlotValueHelper.Truncate(name).Trim();

    /// <summary>
    /// Builds one catalog entry under the shared construction contract. For the
    /// enriched types the JF-684 append is verified structurally (see
    /// AssertArtistEnrichment), so a drifted enrichment path fails the sync
    /// loudly instead of regressing silently on-device.
    /// BLANK-NAME CONTRACT BOUNDARY (JF-695): this factory deliberately does NOT
    /// filter whitespace-only names; every CALLER owns that skip at its own
    /// source (<see cref="CatalogPayload.FromItems"/> skips blank names,
    /// CatalogSeedEnrichment.MergeSeeds skips blank seed values after truncation,
    /// and LibrarySyncService pre-filters item names before the tuple feed). A
    /// whitespace name reaching Create would ship a blank catalog entry silently
    /// (PartialNameSynonyms.Generate returns null for it, so even the structural
    /// guard stays quiet); a future third construction site must pre-filter the
    /// same way, or decide the blank-name contract explicitly (skip vs reject)
    /// in the same change that adds the site.
    /// </summary>
    /// <param name="type">The catalog type the entry belongs to.</param>
    /// <param name="itemId">The Jellyfin item guid, or the deterministic seed guid.</param>
    /// <param name="name">The raw, untruncated display name.</param>
    /// <param name="synonyms">The generated synonym family; may be empty.</param>
    /// <returns>The enriched catalog entry.</returns>
    internal static CatalogValue Create(CatalogType type, Guid itemId, string name, List<string> synonyms)
    {
        var value = new CatalogValue
        {
            Id = CatalogValue.FormatId(type, itemId),
            Name = new CatalogValueName
            {
                Value = SlotValueHelper.Truncate(name),
                Synonyms = synonyms.Count > 0 ? synonyms.Select(SlotValueHelper.Truncate).ToList() : null
            }
        };

        // JF-684: after the phonetic family (so it never consumes the per-name
        // variant cap), append the bare first-word synonym, then verify it
        // structurally. The Artist-only scope is stated once (AppliesTo) and
        // gates both the append and the guard; AppendTo's internal gate stays as
        // the policy owner's own defense for direct callers.
        if (PartialNameSynonyms.AppliesTo(type))
        {
            PartialNameSynonyms.AppendTo(value.Name, type);
            AssertArtistEnrichment(value.Name);
        }

        return value;
    }

    /// <summary>
    /// The structural guard on the JF-684 enrichment: an Artist entry whose
    /// (truncated) name yields a partial word MUST carry it among its synonyms.
    /// The expectation is re-derived from the payload side, so the drift this can
    /// fire on is between the enrichment VERDICT and the synonym-list mutation
    /// (AppendTo's plumbing, AppendDistinct) plus non-determinism; Generate-policy
    /// drift (a changed accept/reject table) is invisible here by construction
    /// (both sides call the same function) and is owned by the
    /// PartialNameSynonymsTests accept/reject pins instead. The throw
    /// (<see cref="CatalogPayloadInvariantException"/>) freezes ONLY its own
    /// catalog type for the run (JF-695 per-type isolation in
    /// LibrarySyncService.RunLegAsync): the frozen type mints no version and its
    /// catalog id is not forwarded to the model injection, so the live model
    /// keeps that type's last-good pinned catalog reference, while the sibling
    /// types and the locale's model injection continue. The freeze itself is
    /// deterministic on every run until the drift is fixed: per-entry degradation
    /// would ship incomplete catalogs. Cost: one extra Generate call per Artist
    /// entry, sync-path only.
    /// </summary>
    /// <param name="name">The freshly built entry name block.</param>
    /// <remarks>Internal for the InternalsVisibleTo test seam: the guard's own
    /// throw contract is pinned directly, since every other pin exercises only
    /// the passing side.</remarks>
    internal static void AssertArtistEnrichment(CatalogValueName name)
    {
        string? expected = PartialNameSynonyms.Generate(name.Value);
        if (expected == null)
        {
            return;
        }

        string truncated = SlotValueHelper.Truncate(expected);
        if (name.Synonyms?.Contains(truncated, StringComparer.OrdinalIgnoreCase) != true)
        {
            throw new CatalogPayloadInvariantException(
                $"Artist catalog entry '{name.Value}' yielded partial synonym '{truncated}' but it is absent from the entry's synonyms; the JF-684 enrichment path is broken.");
        }
    }
}
