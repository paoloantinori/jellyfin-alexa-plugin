---
id: JF-695
title: >-
  JF-695 - JF-689 code-review residuals: per-type sync-leg isolation, and the
  blank-name construction-contract boundary
status: Done
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
- [x] #1 dotnet build passes with 0 errors (full solution, both TFMs; 0 errors, the single xUnit1030 warning at VideoAudioControllerTests.cs:1291 pre-exists on an untouched line)
- [x] #2 dotnet test passes (final state: 4899/4899 net9.0 and 4899/4899 net10.0; baseline 4895 + 4 new pins)
- [x] #3 No new compiler warnings introduced (only the pre-existing xUnit1030 at VideoAudioControllerTests.cs:1291)
<!-- DOD:END -->

## Final Summary

Landed (worktree branch, not merged):

1. PER-TYPE SYNC-LEG ISOLATION. New internal `CatalogPayloadInvariantException :
   InvalidOperationException` (Catalog/CatalogPayloadInvariantException.cs), thrown by
   `AssertArtistEnrichment` (the historical InvalidOperationException contract is kept
   via the base class and re-pinned by `ThrowsAny` plus a new exact-type coupling pin
   in CatalogValueFactoryTests, because xUnit's `Assert.Throws<T>` requires the exact
   type and the per-type isolation catches the derived one specifically). The guard's
   doc now states the per-type blast radius instead of the whole-leg abort.
   `RunLegAsync` (LibrarySyncService.cs) wraps each catalog type in its own try via the
   `SyncTypeLegAsync` local helper: the invariant exception freezes ONLY its type (logs
   an error, returns Version null), while the sibling types continue and the model
   injection still runs. The freeze reuses the existing JF-495 null-version rule
   (Version null -> catalog id not forwarded -> `InjectCatalogReferences` leaves the
   live model's existing reference), so last-good stays pinned for the failing type
   with no new mechanism at the injection site. EVERY other failure keeps the
   whole-leg handling unchanged (the 401 refresh-retry filter, transient-fetch
   exhaustion, timeouts) because the catch is narrowed to the derived type; the
   existing `MidSync401` SeriesTests pin plus a new non-invariant-fails-whole-leg pin
   lock this. Partial failure is honest: `SyncResult.FrozenTypes` (get-only over a
   private HashSet, internal `RecordFrozenType` writer) accumulates the frozen types;
   `result.Success = localesSucceeded > 0 && result.FrozenTypes.Count == 0` so
   CatalogSyncTask never stamps LastCatalogSync over a permanently broken payload
   build; a per-locale warning and a run-level error name the frozen types.
   ACCEPTED CADENCE COST (code-review, acknowledged at the Success gate comment):
   while the drift persists, the restart trigger re-runs the full sync on every
   restart and the healthy types mint fresh catalog versions each time (pre-JF-695
   the same drift aborted every leg at the artist payload build with zero SMAPI
   writes); the healthy types keeping their pins IS the feature, and the drift is a
   code bug to fix promptly.

2. BLANK-NAME CONSTRUCTION-CONTRACT BOUNDARY (documentation decision, no runtime
   change): `CatalogValueFactory.Create`'s doc now states the boundary - the factory
   deliberately does NOT filter whitespace-only names; the two production callers own
   the skip (CatalogPayload.FromItems's IsNullOrWhiteSpace skip verified at its line
   40, CatalogSeedEnrichment.MergeSeeds's skip after truncation verified at its line
   205, LibrarySyncService pre-filters item names at the tuple feed); a future third
   construction site must pre-filter the same way or decide the contract explicitly
   in the same change. All Create callers re-grepped: only the two production sites
   plus one test call, so no reachable unfiltered path exists today (the adjudication
   stands; no runtime blank handling added).

TESTS. New `LibrarySyncServiceLegIsolationTests` (fake SMAPI handler serving all
three catalog types, red-green: the isolation pin FAILS against the pre-change
single-try shape and goes green after): (1) artist invariant violation -> album+series
versions minted and pinned into the model PUT, artist slot type untouched, no artist
catalog/id/version, Success=false with FrozenTypes=[Artist], ledger records the PUT,
error log names the type; (2) non-invariant HttpRequestException in one type fails
the WHOLE leg with no PUT and an empty FrozenTypes (no over-catching); (3) the freeze
repeats deterministically on a second sync run while healthy types re-mint. Plus the
exact-type coupling pin in CatalogValueFactoryTests. The violations are simulated
through the internal `TypeLegEntryProbeForTest` seam (null in production, fired
inside the isolation try; the real factory cannot produce them by construction since
AppendTo and the guard re-derive from the same Generate).

GATES. /simplify (4 angles, parallel): 6 applied - the SyncResult-owned aggregate
(deleted the shadow runFrozenTypes set), the `string?` wrapper return (dead Count
element), rationale dedup (one full statement at the isolation try + the SyncResult
doc; cross-references elsewhere), the SyncTime doc/setter honesty fix, the redundant
IsAssignableFrom removal, and the test arrange dedup + the user-fixture hoist into
`TestHelpers.CreateSyncUser` (the repo's hoist-on-the-third-copy convention, both
pre-existing private copies migrated). 1 skipped with reason: the new file's
FakeSmapiHandler near-twin of SeriesTests' fake - per-file SMAPI fakes are the
established test convention (13 exist); consolidation would churn SeriesTests outside
this diff. Efficiency angle clean. /code-review (high): 5 findings - F1 applied (the
new suite pinned CatalogSyncLocales=string.Empty; with the default "*" every sync
call made a real manifest GET to Amazon and leaned on the fallback catch),
F2 applied (CreateSyncUser defaulted to an EXPIRED device token via
CreateTestDeviceToken's 1970 default, silently entering the refresh path in every
migrated sync test; far-future expiry restored), F5 applied (FrozenTypes doc softened
to name LibrarySyncService as the enforcing caller - the DTO cannot enforce the
Success coupling), F3 acknowledged (the cadence cost above, recorded at the Success
gate comment and here), F4 FILED as JF-703 (a model PUT dropped by the 401-retry via
all-null hash-skip versions reads as a clean locale completion; PRE-EXISTING
injection-gate shape, JF-695 only adds a reachability variant, so fixing the leg
semantics is out of scope here).
