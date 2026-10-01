---
id: JF-695
title: >-
  JF-695 - JF-689 code-review residuals: per-type sync-leg isolation, and the
  blank-name construction-contract boundary
status: To Do
assignee: []
created_date: '2026-10-01 12:00'
labels:
  - catalog
  - cleanup
  - resilience
dependencies:
  - JF-689
references: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-01 same-turn by the JF-689 worker (number reserve; max existing was
JF-694). Carries the two code-review (high) findings from JF-689 that were adjudicated
NOT to apply inside that task, so they are tracked rather than dropped. Read both
against the landed JF-689 shape (CatalogValueFactory + AssertArtistEnrichment + the
assembly-scan pin) before acting.

1. PER-TYPE SYNC-LEG ISOLATION. The JF-689 structural guard throws
InvalidOperationException inside CatalogPayload.FromItems; LibrarySyncService.RunLegAsync
runs artist, album, series, and the model injection inside ONE try block
(LibrarySyncService.cs:155-198, catch at :231), so a deterministic payload-build
invariant failure in the ARTIST type freezes ALL catalog types and every locale's model
injection on every sync run (all legs fail identically, result.Success=false, last-good
versions stay pinned). Deliberate freeze semantics for a code-invariant violation (the
alternative, per-entry degradation, ships incomplete catalogs), but the reviewer's
residual is real: a per-type try inside the leg (one try around each
SyncCatalogForLocaleAsync call) would keep album/series/model-injection alive when one
type's payload build is broken. NOT applied in JF-689: outside the pure-extraction
scope, changes the leg's error-granularity contract, no drive-by refactors. If this is
picked up: the throw's blast radius is documented on AssertArtistEnrichment, and the
byte-identity pins make a payload regression CI-visible before any deploy.
2. BLANK-NAME CONSTRUCTION-CONTRACT BOUNDARY. The whitespace-name skip lives only in
CatalogPayload.FromItems (and LibrarySyncService pre-filters item names at the source,
LibrarySyncService.cs:358-360; ExtractSeedNames trims/IsNullOrWhiteSpace-filters seeds),
NOT in CatalogValueFactory.Create. A future third Artist-catalog site that routes
through the factory (satisfying the JF-689 assembly-scan pin) but forgets the
whitespace skip would ship blank catalog entries (Generate returns null, so even the
structural guard is silent). NOT applied in JF-689: with both real feeds pre-filtering,
any in-factory blank handling is unreachable dead code today, and a null-returning
Create churns both call sites for no reachable behavior change. If a third
construction site ever appears, decide the blank-name contract THEN (skip like
FromItems, or reject), in the same change that adds the site.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
<!-- DOD:END -->
