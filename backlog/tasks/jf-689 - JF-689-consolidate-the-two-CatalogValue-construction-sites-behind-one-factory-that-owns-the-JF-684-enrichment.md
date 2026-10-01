---
id: JF-689
title: >-
  JF-689 - consolidate the two CatalogValue construction sites behind one factory
  that owns the JF-684 partial-synonym enrichment
status: In Progress
assignee: []
created_date: '2026-09-30 20:20'
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

## Fix notes (worker, 2026-10-01)

CHOSEN SHAPE: `CatalogValueFactory.Create(type, itemId, name, synonyms)` (new internal
static class, Alexa/Catalog/CatalogValueFactory.cs) is the ONE CatalogValue
construction path: FormatId, the 140-char Truncate on the value and every synonym, the
count>0-else-null synonym normalization, and the JF-684 PartialNameSynonyms.AppendTo
call all live there. `CatalogPayload.FromItems` and `CatalogSeedEnrichment.MergeSeeds`
both route through it and their inline copies are deleted. The construction body is the
verbatim legacy code, so payload bytes are identical by construction.

GUARD (the task's "structural assertion" invitation, taken): for Artist entries the
factory re-derives the expected partial word via PartialNameSynonyms.Generate and
`AssertArtistEnrichment` throws InvalidOperationException when it is absent from the
synonym list (OrdinalIgnoreCase, the AppendDistinct comparer). Scope and guard gate on
ONE `PartialNameSynonyms.AppliesTo(type)` predicate (extracted in this change), so a
deliberate JF-508 scope widening moves both together. Honest limits, documented on the
method: the guard detects verdict-vs-mutation drift (AppendTo plumbing, AppendDistinct)
and non-determinism, NOT Generate-policy drift (both sides call the same function; that
table is owned by the PartialNameSynonymsTests accept/reject pins); the throw aborts
the whole per-locale sync leg, deterministically, keeping every last-good catalog
version pinned.

THIRD LAYER, the bypass closure: `CatalogValue_HasNoConstructionSiteOutsideTheFactory`
(CatalogValueFactoryTests) mirrors the WarmingGateCoverageTests IL-scan discipline via
the shared IlCallScanner and fails the build on any production `new CatalogValue`
outside the factory, closing the doc's "unless it bypasses this factory entirely"
escape hatch. RED PROOFS run and read: (1) AppendTo call dropped from the factory ->
all 3 factory pins failed AND the guard threw with the exact message; (2) a `new
CatalogValue()` planted in FromItems -> the IL pin failed naming
`CatalogPayload.FromItems`; both reverted, filtered sets green.

BYTE-IDENTITY EVIDENCE: three pins hold the factory and BOTH builder sites byte-for
byte against `LegacyConstruction` (the pre-JF-689 inline shape kept verbatim as the
oracle, forbidden to delegate to the factory): entry-level corpus (enrichment lands,
null-normalization, gate-reject name, Album no-op, >140-char truncation), the full
FromItems payload (whitespace skip + ordering), and the MergeSeeds payload driven
through the real site (StableSeedGuid + raw seed threading). Suites 4852/4852 net9.0 +
net10.0 (baseline 4847 + 5 new pins: 3 byte-identity, 1 IL scan, 1 guard throw
contract), full build 0 warnings/0 errors.

GATES: Skill simplify (4 agents: reuse/simplification/efficiency/altitude). Applied:
the seed pin now drives the real MergeSeeds site instead of calling the factory
directly; duplicated call-site comments trimmed (CatalogPayload comment deleted, seed
site keeps the one seed-specific sentence); factory method doc no longer re-enumerates
the class doc; CLAUDE.md 140-char gotcha pointer moved from CatalogPayload to
CatalogValueFactory; the IL-scan bypass pin added; AppliesTo extracted. Skipped with
justification: the AppendTo-returns-word guard shape (a return value certifies nothing
about the append landing and passes vacuously if the call is deleted; the re-derivation
is the structural point; cost measured well under 0.5s per weekly sync), the first
pin's Assert.True + message (names the drifted entry, which Assert.Equal cannot), the
two efficiency footnotes (seed double-Truncate inherent to the factory owning
truncation; 6-element linear Contains) as measured-negligible. Skill code-review high:
5 findings, 3 applied (guard doc corrected: Generate-policy drift is invisible by
construction and owned by the accept/reject pins; blast radius documented honestly;
single AppliesTo gate block; internal test seam + `ArtistEnrichmentGuard_MissingPartial
Word_Throws` pinning the throw path, the null-synonyms branch, and the OrdinalIgnoreCase
comparer), 2 not applied and filed SAME-TURN as JF-695 (per-type sync-leg isolation so
one broken type does not freeze album/series/model-injection; the blank-name
construction-contract boundary decision for a future third site). DoD 4-8 N/A (pure
extraction: no session attrs, no HttpClient, no interaction model, no intent/handler,
no locale strings). No deploy; do not push.
