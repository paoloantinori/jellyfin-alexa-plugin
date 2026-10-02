---
id: JF-689
title: >-
  JF-689 - consolidate the two CatalogValue construction sites behind one
  factory that owns the JF-684 partial-synonym enrichment
status: Done
assignee: []
created_date: '2026-09-30 20:20'
updated_date: '2026-10-02 11:51'
labels:
  - catalog
  - cleanup
dependencies:
  - JF-684
references:
  - >-
    backlog/tasks/jf-684 -
    JF-684-catalog-musician-slot-blocks-intent-selection-for-non-catalog-values-bare-artist-names-produce-NO-intent-fuzzy-tiers-voice-unreachable.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn by the JF-684 worker (hand-created in the worker worktree per
the number reserve; max existing was JF-688). Source: the JF-684 /simplify altitude
review, which named this residual and recommended NOT folding it into JF-684 (smallest
change, no drive-by refactor).

The invariant "an Artist catalog payload entry carries the JF-684 partial-name synonym"
is enforced at TWO parallel construction sites with no structural tie:
`CatalogPayload.FromItems` (library items) and `CatalogSeedEnrichment.MergeSeeds` (static
seeds). Both call `PartialNameSynonyms.AppendTo(name, type)` explicitly, and both also
duplicate the pre-existing construction shape (SlotValueHelper.Truncate on value and
synonyms, null-vs-empty synonym normalization). The JF-684 review judged the deeper fix:
a shared internal CatalogValue factory used by both sites with AppendTo inside it, so a
future THIRD Artist-catalog construction site (a new entity type wired into catalog
sync, or a manual catalog-upload admin endpoint) cannot ship without the partial
synonym. Today such a site would regress silently on-device (the closed selection gate
JF-684 diagnosed), with nothing failing at build time.

Guardrail while this stays open: both current sites have direct tests pinning the
enrichment (`FromItems_ArtistCatalog_PinkFloydCarriesPinkSynonym`,
`MergeSeeds_ArtistSeed_CarriesPartialSynonym`), and the JF-684 CLAUDE.md note points
at PartialNameSynonyms as the mechanism. If a third Artist-catalog construction site
is ever added, wire AppendTo there in the same change or fix this task first.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Status-sync closure during the 2026-10-02 backlog audit: landed and fully verified in its own cycle, status never flipped. Merge into main: 26ac8dcc (worker commit dd6dcc71). The ONE CatalogValue construction path (CatalogValueFactory.Create owning the JF-684 partial-synonym enrichment with the AssertArtistEnrichment structural guard and the IlCallScanner assembly-scan pin); its two code-review residuals were filed same-turn as JF-695 and JF-702-adjacent context, both since handled (JF-695 landed 2026-10-02). No deploy was needed at the time (payload bytes identical by construction).
<!-- SECTION:FINAL_SUMMARY:END -->
