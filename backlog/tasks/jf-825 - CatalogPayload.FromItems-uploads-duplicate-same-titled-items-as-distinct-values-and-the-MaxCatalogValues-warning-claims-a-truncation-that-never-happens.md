---
id: JF-825
title: >-
  CatalogPayload.FromItems uploads duplicate same-titled items as distinct
  values (audiobooks are the shape that bites: single-file edition + chaptered
  edition), and the MaxCatalogValues warning claims a truncation that never
  happens
status: To Do
labels: [catalog-sync]
---

## Description

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
