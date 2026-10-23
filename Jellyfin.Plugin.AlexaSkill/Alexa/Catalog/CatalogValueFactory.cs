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
    /// AssertPartialWordEnrichment), so a drifted enrichment path fails the sync
    /// loudly instead of regressing silently on-device.
    /// BLANK-NAME CONTRACT (JF-695, revised by issue #41): Create returns NULL
    /// when the name sanitizes to whitespace (blank source names, and names
    /// consisting only of Amazon-rejected characters; both callers skip nulls).
    /// The pre-#41 caller-side skips stay as they were; the factory-level gate
    /// is the defense that covers every future construction site automatically,
    /// because a blank entry would ship silently otherwise (PartialNameSynonyms
    /// .Generate returns null for it, so even the structural guard stays quiet).
    /// </summary>
    /// <param name="type">The catalog type the entry belongs to.</param>
    /// <param name="itemId">The Jellyfin item guid, or the deterministic seed guid.</param>
    /// <param name="name">The raw, untruncated display name.</param>
    /// <param name="synonyms">The generated synonym family; may be empty.</param>
    /// <returns>The enriched catalog entry.</returns>
    internal static CatalogValue? Create(CatalogType type, Guid itemId, string name, List<string> synonyms)
    {
        // Issue #41: a name that sanitizes to EMPTY (only rejected characters)
        // produces no catalog entry at all; the callers skip nulls.
        string prepared = SlotValueHelper.Truncate(name);
        if (string.IsNullOrWhiteSpace(prepared))
        {
            return null;
        }

        var value = new CatalogValue
        {
            Id = CatalogValue.FormatId(type, itemId),
            Name = new CatalogValueName
            {
                Value = prepared,
                // Issue #41: a synonym that sanitizes to empty is dropped, not pushed.
                Synonyms = synonyms.Count > 0
                    ? synonyms.Select(SlotValueHelper.Truncate).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                    : null
            }
        };

        // JF-684: after the phonetic family (so it never consumes the per-name
        // variant cap), append the bare first-word synonym, then verify it
        // structurally. The scope (Artist + Movie since JF-874) is stated once
        // (AppliesTo) and gates both the append and the guard; AppendTo's
        // internal gate stays as the policy owner's own defense for direct
        // callers.
        if (PartialNameSynonyms.AppliesTo(type))
        {
            PartialNameSynonyms.AppendTo(value.Name, type);
            AssertPartialWordEnrichment(value.Name);
        }

        return value;
    }

    /// <summary>
    /// The structural guard on the JF-684 enrichment: an enriched-type entry
    /// whose (truncated) name yields a partial word MUST carry it among its
    /// synonyms. The expectation is re-derived from the payload side, so the
    /// drift this can fire on is between the enrichment VERDICT and the
    /// synonym-list mutation (AppendTo's plumbing, AppendDistinct) plus
    /// non-determinism; Generate-policy drift (a changed accept/reject table) is
    /// invisible here by construction (both sides call the same function) and is
    /// owned by the PartialNameSynonymsTests accept/reject pins instead. The
    /// throw (<see cref="CatalogPayloadInvariantException"/>) freezes ONLY its
    /// own catalog type for the run (JF-695 per-type isolation in
    /// LibrarySyncService.RunLegAsync): the frozen type mints no version and its
    /// catalog id is not forwarded to the model injection, so the live model
    /// keeps that type's last-good pinned catalog reference, while the sibling
    /// types and the locale's model injection continue. The freeze itself is
    /// deterministic on every run until the drift is fixed: per-entry degradation
    /// would ship incomplete catalogs. Cost: one extra Generate call per enriched
    /// entry, sync-path only.
    /// </summary>
    /// <param name="name">The freshly built entry name block.</param>
    /// <remarks>Internal for the InternalsVisibleTo test seam: the guard's own
    /// throw contract is pinned directly, since every other pin exercises only
    /// the passing side.</remarks>
    internal static void AssertPartialWordEnrichment(CatalogValueName name)
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
                $"Catalog entry '{name.Value}' yielded partial synonym '{truncated}' but it is absent from the entry's synonyms; the JF-684 enrichment path is broken.");
        }
    }
}
