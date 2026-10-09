---
id: JF-825
title: >-
  CatalogPayload.FromItems uploads duplicate same-titled items as distinct
  values (audiobooks are the shape that bites: single-file edition + chaptered
  edition), and the MaxCatalogValues warning claims a truncation that never
  happens
status: Done
assignee: []
created_date: ''
updated_date: '2026-10-09 23:06'
labels:
  - catalog-sync
dependencies: []
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed by the JF-823 worker (2026-10-09) from the /code-review high gate,
findings F2 and F3. Both live in the pre-existing payload-build path that
JF-823 extended with a fourth type; both are filed rather than fixed in the
JF-823 diff to keep that diff a pure fourth-type wiring.

**F2, duplicate names.** `CatalogPayload.FromItems` (CatalogPayload.cs, the
loop over the item tuples) has no case-insensitive name dedup, unlike the seed
merge (CatalogSeedEnrichment.MergeSeeds dedups among seeds and library-wins on
collision). A library holding "Sapiens" both as a single-file AudioBook leaf
and as a chaptered AudioBook folder uploads two `Sapiens` values with different
`jellyfin_audiobook_` ids; entity resolution picks one arbitrarily. Decide the
policy (library-wins like the seeds, i.e. keep the FIRST in fetch order, or
prefer the item shape PlayBook handles better) and dedup inside FromItems or at
the sourcing loop, with the same rule for all four types.

**F3, the lying warning.** `LibrarySyncService.SyncCatalogForLocaleAsync`
(around the `payload.Values.Count >= MaxCatalogValues` check) logs "Truncated
{Type} catalog to {Limit} items" but NOTHING truncates: the payload exceeds
MaxCatalogValues when items-plus-seeds pass the fetch limit, the log asserts a
truncation that never happened, and SMAPI receives the oversized payload. Either
actually truncate to MaxCatalogValues (keeping the warning true) or reword the
warning to state the payload exceeds the cap. Fix at the one site so all four
types are covered.

GATE-MARKER ADDITION (2026-10-09, JF-823 marker finding 5): the JF-823
audiobook payload appends its 22 seed values AFTER the MaxCatalogValues-bounded
library fetch, so the audiobook leg's unbounded side grew by exactly the seed
set; the cap-enforcement gap below is unchanged in kind, one constant larger.

## Implementation log (2026-10-09, worker)

FIX, both defects coherently:

- **F2 dedup** (`CatalogPayload.FromItems`): same-named library items collapse
  to ONE value. Key = `CatalogValueFactory.CanonicalNameKey(name)` (the
  140-char truncated value TRIMMED, compared OrdinalIgnoreCase), FIRST
  occurrence wins so the survivor keeps the real Jellyfin id of the first item
  in fetch order (the SortName-ascending library-priority order). Same rule for
  all four types by construction: FromItems is the ONE library-value
  construction path (JF-689 factory convention, altitude-checked). The seed
  merge keeps its library-wins rule and now derives its key from the SAME
  helper, so the two dedup sites cannot drift.
- **F3 cap** (`LibrarySyncService.SyncCatalogForLocaleAsync` +
  `CatalogPayload.TruncateTo`): the cap now binds the FINAL payload (library
  values + seeds + generic word, after dedup), enforced by a tail cut that
  returns the dropped entries. Payload order IS priority order (library in
  fetch order, then seed titles, generic word last), so the cut drops the
  generic word first, then seeds, then the library tail. The warning fires
  EXACTLY when something was dropped and names what: dropped count, the exact
  static-seed vs library split, and a 5-name sample. A second, separate
  fetch-saturation warning (code-review F2) restores the signal the old >=
  check accidentally carried for seedless types (Series) whose payload lands
  exactly at the cap with nothing dropped. The per-type dedup also logs its
  collapsed-item count at Debug (the debug-logging policy).
- **Default untouched**: MaxCatalogValues stays the private const 50000 (not
  config-backed; grep of Configuration/ confirms). A `MaxCatalogValuesForTest`
  seam (the InterLocaleDelayMsForTest pattern) lets the pins drive a full sync
  past a small cap; BOTH cap sites (fetch Limit + truncation) read
  `EffectiveMaxCatalogValues`, and the existing 50000 fetch-limit pin stays
  green.

RED-GREEN (both TFMs, net10.0 first): four pins red on the pre-fix tree
(seam scaffolding only, behavior identical to production): the duplicate-upload
pin (expected 2 values, actual 4), the unbounded-tail pin (expected 5, actual
30; and 6 vs 26), and the boundary pin whose red output shows the OLD warning
"Truncated Audiobook catalog to 24 items" firing on an exactly-at-cap payload
that was never truncated. All green after the fix. A fifth pin (whitespace-
padded duplicates, code-review F3) ran red against the trimless key (2 values
for "Sapiens"/"Sapiens ") and green after CanonicalNameKey.

VERIFIERS: Release build 0 warnings 0 errors; ONE full `dotnet test` (no -f)
BOTH TFMs: 5589/5589 net10.0 AND 5589/5589 net9.0 (baseline 5585 + 4; the F3
pin arrived after the review gate, covered by the 175-test catalog-adjacent
run and the merge-time suite).

GATES. /simplify (4 agents): APPLIED 5: the TruncateTo dropCount hoist plus
collection-expression empty return; the SortName-asc doc clause (priority is
the fetch order, not a designed model); the library-leg-canary comment
(nonzero library drops are impossible while fetch Limit == cap, so the leg is
a bypass canary); test dead-cargo removal (unused Requests list, unused
tryGetValue local) plus a shared TruncationWarnings helper; the second-copy
hoist marker on SetupLibraryWithAudiobooks. SKIPPED 3 with reasons: the double
SlotValueHelper.Truncate per item (not worth widening the JF-689 factory
contract for a length-compare on a once-per-sync path); TruncateTo's GetRange
copy of the dropped tail (a count+sample tuple would serve only the log line
and weaken the dropped-tail contract); the assembly-pipeline hoist into one
owner method (extraction-on-convergence: one enrichment family, one assembly
site, invariant documented at both ends and behaviorally pinned; trigger = a
second enrichment family or assembly site).

/code-review high (5 findings): F1 REJECTED and FILED as JF-848 (the cap
drop order was specified by the dispatch, seeds and generic words first; the
reviewer's reserved-slots alternative is a product tradeoff for >50000-value
libraries, not a bug); F2 APPLIED (fetch-saturation warning, pinned); F3
APPLIED (CanonicalNameKey trims; the ER read side GetCanonicalValues already
dedups by Trim, so the two sides now agree; red-green pin added); F4 signal
half APPLIED (Debug line for dedup-collapsed items), count-semantics half
REJECTED (the per-type count stays the FETCH count by design; restructuring
StoreCount across the wiring table is outside the fix); F5 APPLIED
(CanonicalNameKey shared by FromItems and MergeSeeds makes the key coupling
structural; subsumes the simplify-side hoist-on-third judgment because F3 made
the key non-trivial).
<!-- SECTION:NOTES:END -->
<!-- SECTION:DESCRIPTION:END -->
