---
id: JF-695
title: >-
  JF-695 - JF-689 code-review residuals: per-type sync-leg isolation, and the
  blank-name construction-contract boundary
status: Done
assignee: []
created_date: '2026-10-01 12:00'
updated_date: '2026-10-02 08:17'
labels:
  - catalog
  - cleanup
  - resilience
dependencies:
  - JF-689
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
- [x] #1 dotnet build passes with 0 errors (full solution, both TFMs; 0 errors, the single xUnit1030 warning at VideoAudioControllerTests.cs:1291 pre-exists on an untouched line)
- [x] #2 dotnet test passes (final state: 4899/4899 net9.0 and 4899/4899 net10.0; baseline 4895 + 4 new pins)
- [x] #3 No new compiler warnings introduced (only the pre-existing xUnit1030 at VideoAudioControllerTests.cs:1291)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 1a48a53d (per-type sync-leg isolation: CatalogPayloadInvariantException narrows the freeze to one catalog type while siblings and the model injection continue; FrozenTypes gates Success so LastCatalogSync never advances over a broken build; blank-name contract documented on CatalogValueFactory.Create), worker gates green (simplify 6 applied; code-review high 3 applied, 1 acknowledged, 1 filed as JF-703), worker suites 4899/4899 both TFMs, orchestrator independent suite 4899/4899 both TFMs on the worker commit, orchestrator gate-marker review 6 findings adjudicated same-turn (4 applied in the review tail db729402: frozen clause on the completion log line, probe reachability guard items.Count>0, CatalogSyncLocales save/restore in Dispose, order-independent FrozenTypes asserts; 2 filed as JF-705 ledger observability and JF-706 type-leg de-triplication; filtered classes 27/27 after the tail), merged into main as 62ec9132 with the merged-tree full suite 4899/4899 both TFMs exit 0 (net10.0 line shown, net9.0 carried by the exit code). Production surface changed (LibrarySyncService, CatalogValueFactory): deploys with the JF-690 merge in the single post-merge deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
