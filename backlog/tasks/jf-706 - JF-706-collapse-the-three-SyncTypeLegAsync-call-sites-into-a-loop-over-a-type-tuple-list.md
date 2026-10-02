---
id: JF-706
title: >-
  JF-706 - collapse the three SyncTypeLegAsync call sites into a loop over a
  type-tuple list so a fourth catalog type cannot be partially wired
status: To Do
assignee: []
created_date: '2026-10-02 10:12'
labels:
  - catalog
  - code-quality
dependencies:
  - JF-695
references:
  - >-
    backlog/tasks/jf-695 -
    JF-695-JF-689-code-review-residuals-per-type-sync-leg-isolation-and-the-blank-name-contract-boundary.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-695 merge
(commit 1a48a53d, finding 4 of 6). The JF-695 per-type isolation introduced three
near-identical `SyncTypeLegAsync` call sites in `RunLegAsync`
(LibrarySyncService.cs:222-233: Artist/Album/Series, each differing only in
(type, items, stored catalog id, name, description)) plus a hand-written three-operand
`||` chain gating the model injection and six positional id/version arguments into
`UpdateInteractionModelAsync`. That is the repo's named "missed one" bug class: adding a
fourth catalog type (a change class this repo has repeatedly made) requires editing three
parallel blocks plus the injection call; missing one site compiles clean and freezes or
syncs inconsistently.

THE WORK: a loop over a tuple list `(CatalogType, items, storedId, name, description)`
that collects the minted versions, with the model-injection gate and the
`UpdateInteractionModelAsync` arguments DERIVED from the same collection rather than
hand-written per type. Mind: `UpdateInteractionModelAsync` takes per-type id/version
parameters (not a dictionary), so deriving means either building the arguments from the
collection with explicit per-type extraction at ONE place, or widening that signature to
accept the collection; prefer the former (smaller change, keeps the CatalogManager
contract stable). Pins must stay green unchanged (the three isolation pins, the exact-type
coupling pin, and the JF-495 SeriesTests mid-sync pin). This was deliberately NOT done in
the JF-695 review tail: it reshapes the leg flow the JF-695 pins encode, so it deserves
its own red-green pass, not a drive-by.

AUDIT UPDATE (2026-10-02): path/line refresh - the file lives at Alexa/Catalog/LibrarySyncService.cs; the three call sites are at 219-229, the three-operand gate at 235, the six positional injection args at 247-252. Substance unchanged.

GATE-MARKER TAIL (2026-10-02, orchestrator review of commit 1a2f7651, 5 findings): all
four scrutiny axes verified clean (the live-getter sound with the per-type guard making
cross-type writes unreachable and the post-leg capture final incl. create-then-fail and
the 401 attempt-2 re-run; minted.Count > 0 exactly the old three-operand chain with the
JF-495 rule structural; the pins honest and RUN-EXECUTED green; the JF-709 starvation
analysis code-confirmed end to end). F1 APPLIED (JF-709 priority raised to high with the
audit-confirmation block); F2 APPLIED as a JF-703 audit addendum (the
hash-record-before-upload ordering makes the skip "already attempted", not "already
uploaded", and the ~578 comment false in the failed-upload-retry shape); F5 APPLIED (the
corrected JF-513.3 comment now names the real equivalence classes instead of the
ar-SA/hi-IN example that contradicts the JF-543 filter above it); F4 APPLIED (the pin-2
doc now states the direct-call boundary: a method-group delegate or reflection
invocation is invisible to the call-token scan); F3 FILED as JF-716 (the injection seam
- gate plus six positional arguments - can re-expand to hand ternaries with both pins
green; candidate shape: a negative ldfld assertion that RunLegAsync's body never loads
the three stored-id getters).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (test project build with both TFMs, 0 errors; only the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337, untouched line)
- [x] #2 dotnet test passes (final state: 4929/4929 net9.0 and 4929/4929 net10.0, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` exit 0; baseline 4927 + 2 new structure pins)
- [x] #3 No new compiler warnings introduced (every post-change build grepped clean apart from the pre-existing xUnit1030 pair; the lambda-in-tuple and GetValueOrDefault shapes produced no CA analyzers hits)
- [x] #4 N/A (no session attributes touched; the change is LibrarySyncService leg wiring only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (behavior-preserving restructure, no new intent or handler; the existing pins ARE the red-green proof and stayed green UNCHANGED: the 3 JF-695 isolation pins incl. the artist-freeze-survives PUT-wiring pin, the exact-type coupling pin, the JF-495 SeriesTests battery incl. the mid-sync 401 pin and the stale-version guard, and both JF-705 frozen-clause pins with the frozenTypes threading into RecordModelUpdateInLedger intact)
- [x] #8 N/A (no Alexa speech)
- [x] #9 /simplify passed (4 parallel angles; 2 applied: the id folded into the minted collection as (Version, CatalogId) pairs collapsing VersionOf/IdOf and the typeLegs.Single rescan, and the pin's redundant HashSet dropped via Assert.Single's element return; reuse clean, efficiency all-negligible with the live-getter flagged load-bearing, altitude's write-back finding FILED as JF-711 with the wiring-table comment softened to name the remaining per-type site)
- [x] #10 /code-review high passed (5 findings, 4 applied: the stale outer JF-513.3 comment corrected to the actual null return; the pin moved out of the [Collection("Plugin")] fixture into a standalone collection-free LibrarySyncServiceStructureTests class matching the 4 existing IL-scan precedents; the SyncCatalogForLocaleAsync sole-caller assertion added to the same class, closing the direct-call bypass of the isolation try; the wiring table hoisted to sync scope, locale-invariant, per-leg rebuild gone; the 5th finding is pre-existing and landed as an AUDIT UPDATE on JF-709: the JF-513.3 hash-skip starves every later locale of a synonym-equivalence class of its model PUT, 8 of 16 synced locales today, correcting that task's "its model genuinely still references current catalog versions" premise)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the JF-706 worker (worktree agent-a05216a07a98e3859, branch off 8dbb9551). DERIVATION
SHAPE: one per-type wiring table `(CatalogType, items, Func<string?> StoredCatalogId, name,
description)` hoisted to sync scope in SyncUserLibraryAsync (locale-invariant, built once; the id
is a live GETTER because SyncCatalogForLocaleAsync assigns it when creating the catalog, and a leg
writes only its own type's id so the post-leg re-read is final). The leg loop inside RunLegAsync
collects `(Version, CatalogId)` pairs keyed by type into `minted`; the injection gate is
`minted.Count > 0`; the six UpdateInteractionModelAsync arguments are extracted per type at ONE
place via a single `Minted(type)` local function over `minted.GetValueOrDefault` (an absent type
reads as (null, null), making the JF-495 id-only-with-fresh-version rule structural).
CatalogManager's per-type id/version signature untouched, per the task constraint.

PINS: the structural pin ran red first against the pre-change code (3 call instructions to the
SyncTypeLegAsync stub, all inside RunLegAsync's state machine MoveNext; a method-level count
would have read 1 and pinned nothing, which is why the pin counts call instructions) and green
after (exactly 1). A second pin asserts SyncCatalogForLocaleAsync is called only by the type-leg
stub, so the JF-695 isolation try, the probe seam, and the minted bookkeeping cannot be bypassed
by a direct call. Both live in a NEW standalone collection-free class
(LibrarySyncServiceStructureTests) following the 4 existing IlCallScanner precedents; the JF-695
isolation battery and its fixture are untouched. All pre-existing pins green unchanged, three
full-suite runs across the cycle, final 4929/4929 both TFMs (baseline 4927 + 2).

FILINGS from the gates: JF-711 (the catalog-id WRITE-back in SyncCatalogForLocaleAsync is still
a per-type if/else chain, compile-silent for a fourth type; setter-threading design recorded,
with the CatalogManager per-type surface documented as a deliberate boundary) and a JF-709 AUDIT
UPDATE (the JF-513.3 hash-skip starves every later member of a synonym-equivalence class, es x3
and fr x2 and en/hi x6, of its model PUT forever under the default "*" config, 8 of 16 synced
locales today; corrects JF-709's "model genuinely still references current catalog versions"
premise; the stale outer JF-513.3 comment claiming the skip returns the last uploaded version
was fixed in-change). No deploy; do not push; production surface changed (LibrarySyncService), so
the merge should deploy per the standing post-merge practice.
<!-- SECTION:FINAL_SUMMARY:END -->
