---
id: JF-703
title: >-
  JF-703 - catalog sync: a model PUT dropped by the 401-retry reads as a clean
  locale completion (pre-existing injection-gate shape, re-filed by the JF-695
  review)
status: Done
assignee: []
created_date: '2026-10-02 12:00'
updated_date: '2026-10-04 16:27'
labels:
  - catalog
  - resilience
dependencies:
  - JF-695
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn by the JF-695 worker (number reserve; max existing was
JF-701). NOT applied inside JF-695: the shape is PRE-EXISTING (the injection gate
plus the JF-513.3 per-run payload-hash skip predate it); JF-695's diff only touched
the hunk and adds a frozen-type variant of the same trigger.

The sequence (code-review high finding, verified against
LibrarySyncService.RunLegAsync + SyncCatalogForLocaleAsync):

1. Attempt 1 of a locale leg uploads artist+album+series (each payload hash is
   recorded in uploadedPayloadHashes BEFORE the upload), then the model PUT gets a
   401 -> refresh + one full-leg retry.
2. Attempt 2: all three payloads hash-match the attempt-1 entries, so
   SyncCatalogForLocaleAsync skips all three uploads and returns Version null for
   each (the JF-513.3 no-op contract).
3. The injection gate (`artistVersion != null || albumVersion != null ||
   seriesVersion != null`) sees all-null and SKIPS the PUT entirely.
4. The leg returns succeeded. This locale's live model was never updated with the
   attempt-1 versions (those ARE minted on SMAPI, but the model still references
   the old versions), and no log line marks anything amiss.

JF-695's contribution is only reachability shape: a frozen type also contributes a
null version in that window, and pre-JF-695 the frozen artist aborted attempt 1
wholesale so attempt 2 genuinely re-uploaded everything. Without a frozen type the
same drop is reachable today with zero JF-695 involvement.

Fix sketch (when picked up): track "a model PUT was attempted for this locale but
not completed" across the attempt loop; when attempt 2 returns all-null versions
after such an attempt, treat the leg as failed (or re-mint instead of hash-skipping)
so the leg cannot read as a clean completion. Trigger window is narrow (the 401
must land exactly on the PUT), hence low priority.

AUDIT ADDENDUM (2026-10-02, JF-706 gate-marker round, pre-existing but this family):
the hash-skip records the payload hash BEFORE the upload (LibrarySyncService.cs ~596,
upload at ~608), so "already uploaded this run" is really "already attempted this run":
a failed upload followed by the leg-level 401 retry hash-skips a version that was never
minted, dropping a needed upload for the rest of the run (attempt 2 sees the hash at
~586 and returns count/null; other types mint, the PUT omits this id, the locale logs
completed). Self-heals only on the next full sync (fresh dictionary). The in-code
comment at ~578-582 ("the last minted version ... is the one the live model already
references") is FALSE in this shape. Same commit's JF-709 audit extends the starvation
set: the equivalence-class FIRST member can itself be hash-skipped after a
failed-upload retry. Fold this ordering fix into this task's fix shape.
FOLDED INTO JF-709 (2026-10-02, picked up together): the addendum's ordering hazard
(hash-record-before-upload) is fixed by the JF-709 change - the hash is recorded only
after UploadCatalogValuesAsync returns successfully, and the false ~578 comment is
corrected in the same touch. THIS task's remaining scope is the CORE sequence only:
the PUT-completed-vs-skipped distinction across the locale attempt loop when the 401
lands on the model PUT itself (attempt-1 uploads all succeed, attempt-2 hash-skips
them all, the injection gate skips the PUT).
AUDIT ADDENDUM 2 (2026-10-02, JF-709 code-review high round; both pre-existing
retry-shape residuals, same root as the CORE, so they land here rather than as new
tasks): (a) the PUT-stage-401 sequence above also produces NO ledger write and a
green leg - the stale-green-row hole JF-709 closed for the freeze shape is equally
reachable via this retry path; fix it with the CORE (the attempted-PUT tracking
makes the leg fail) or extend JF-709's no-PUT writer to it. (b) A 401 on ONE type's
upload mid-leg leaves the earlier types hash-recorded, so the retry's PUT wires ONLY
the retried type's catalog - on a fresh install the model gets no valueSupplier for
the silently skipped types while the run reports Success; the attempted-upload
tracking must cover the type legs, not only the PUT.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The locale-leg attempt loop distinguishes "PUT completed" from "PUT skipped after a prior PUT attempt", and the latter fails the leg (or re-mints the skipped uploads) deterministically. CLOSED AS ALREADY-FIXED, satisfied structurally since JF-717 (9b612d48, 2026-10-03): "PUT skipped after a prior PUT attempt" is UNREACHABLE, because the JF-513.3 hash-skip returns the run's minted version instead of null, so attempt 2's minted table always fills and the minted.Count > 0 gate always fires the retried PUT with the refreshed token; when the retried PUT itself fails, the generic catch fails the leg (localesFailed, warning log). The addendum's re-mint half (hash recorded only after a successful upload) landed in the JF-709 fold (f44aef8c).
- [x] #2 A pin exists for the sequence: attempt-1 PUT 401 -> attempt-2 all-null versions -> leg reports failure. The DoD's literal shape is unreachable post-JF-717 (attempt 2 is never all-null); the live pin for the sequence is SyncUserLibraryAsync_MidLeg401AfterUpload_RetryPutsFromMemoHit (JF-717, asserting upload-once + PUT-twice + the retried body wired with the run's mint), EXTENDED by this task with the finding's unpinned ledger half (earned-green row via TestHelpers.AssertCatalogSyncLedgerSucceeded + Caveat=None), counterfactually red-proven BOTH ways on both TFMs against the shipped shape (memo null-return red at Expected 2/Actual 1 PUTs; it-IT ledger-write skip red at the bundle's NotNull on a fresh ledger).
- [x] #3 dotnet build 0 errors, dotnet test green both TFMs, no new warnings. Full suite 5143/5143 net9.0 AND net10.0 on the final merged state (main's baseline moved past the dispatch-time 5132 while this branch was in flight, the JF-708-era merges; this branch's delta is test-count-neutral); Release --no-restore -warnaserror 0 warnings 0 errors.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED 2026-10-04 as ALREADY-FIXED (stale at pickup): the finding died with JF-717.

VERDICT AND EVIDENCE: the filed core sequence (attempt-1 uploads mint + the model PUT 401s -> attempt-2 hash-skips return null for all types -> the injection gate sees all-null and skips the PUT -> the leg reads as a clean completion) cannot occur on current main. Two landed changes close it, both AFTER the 2026-10-02 12:00 filing: (1) the JF-709 fold (f44aef8c, 10-02 21:11) fixed the ordering addendum (mintedVersionsByPayload records only after UploadCatalogValuesAsync succeeds, so a failed upload re-uploads instead of hash-skipping a never-minted version); (2) JF-717 (9b612d48, 10-03 05:54) made the hash-skip RETURN the run's shared minted version, so attempt 2's minted table always fills, the minted.Count > 0 gate cannot skip, and the retried PUT fires wired with the attempt-1 mints under the refreshed token. The JF-717 commit itself names this exact second starvation shape with its counterfactual red proof ("a leg whose model PUT 401s AFTER its upload minted used to retry into a silently unwired clean success (empty minted table, no PUT); the memo hit now supplies the mint and the retried PUT fires", 1 vs 2 PUT requests). Payload-key stability across attempts (the memo-hit premise) is machine-pinned by the existing pin's upload-once assert. The 401 propagation premise holds: EnsureSuccessAsync -> EnsureSuccessStatusCode throws HttpRequestException with StatusCode set, caught by the leg-level Unauthorized filter.

ADDENDUM 2 DISPOSITIONS: (a) "green leg + no ledger write": unreachable post-JF-717. When the retried PUT succeeds, the JF-705 PUT path writes the row (an earned green); when it fails, the generic catch FAILS the leg (localesFailed, warning, Success=false when every locale fails) and the previous row keeps the last ACTUAL PUT's truth, which is the generic failed-leg shape shared by every leg failure, not the clean-reading shape JF-709's no-PUT writer targeted (that writer exists for legs that READ clean while performing no PUT). (b) mid-leg upload 401 on one type: the failed type's hash was never recorded (JF-709 ordering) so it re-uploads, while the earlier successful types memo-hit and return their attempt-1 mints (JF-717), so the retried PUT wires ALL types, none silently skipped; the two mechanics are individually pinned (the upload-401 pin asserts all-three wiring + the ledger; the PUT-401 pin asserts memo-hit + retried PUT) and run through the same statements with no per-type branch.

THIS TASK'S DELTA (the closure pin + the fold): the one unpinned half was the finding's own ledger framing. The JF-717 pin now also asserts the ledger row via the new shared bundle TestHelpers.AssertCatalogSyncLedgerSucceeded(locale) (NotNull + Source=CatalogSyncGetModifyPut + SUCCEEDED + Error=null, returning the row for site extras) chained with Caveat=None: the locale's green row is EARNED by the retried PUT. The bundle was extracted at its fourth verbatim site (EquivalenceClass es-US, SeriesTests x2 pre-existing, the extension's it-IT), JF-692 zero-assert-diff at the three pre-existing sites; the two LegIsolation PARTIAL sites stay deliberately unconverted (documented in the helper's doc; converting would add asserts beyond this diff's mandate). NON-VACUITY MECHANISM (the altitude round's correction, verified against PluginCollection/TestHelpers): no sentinel row is needed or used; PluginTestBase's ctor resets Plugin.Instance per test method (xUnit per-method instantiation) and the class ctor's EnsurePluginInstance mints a fresh PluginConfiguration with an empty LocaleModelStatuses, so a non-null it-IT row is by construction THIS run's write. RED PROOFS, both run against the shipped shape on both TFMs: (1) reverting the memo return to null reds the pin at the PUT count (Expected 2, Actual 1, the finding's dropped-PUT shape caught one assert before the ledger bundle); (2) skipping only it-IT's ledger write reds at the bundle's NotNull (Value is null on the fresh ledger), guarding the complementary regression. Both restored, green.

GATES: /simplify 4 agents: reuse APPLIED (the bundle extraction), altitude APPLIED (the first cut's sentinel row was inert protection justified by a false cross-test-persistence model; deleted with the doc rewritten to the true mechanism, and both red proofs re-run against the corrected shape), efficiency CLEAN, simplification CLEAN (per-assert independence verified: Source/Status/Error/Caveat each discriminate a distinct writer regression). /code-review high: 0 correctness bugs; 2 low findings APPLIED (the pin doc's failure-locus sentence corrected: the pre-JF-717 shape reds at the PUT-count assert first, the NotNull independently guards the write-skipped regression; the unconverted partial sites named as a deliberate boundary in the helper's doc). No out-of-scope finding survived (both reviewers' cross-file consolidation candidates were judged on both sides), so the reserved JF-754 number stays unused.

SUITES: touched+adjacent 27/27 both TFMs (EquivalenceClass 4 + Series 10 + LegIsolation 13); full suite ONCE on the final state (worktree tip merged with local main, which by then carried the JF-673/JF-692 merges and the later JF-708-era bookkeeping): 5143/5143 net9.0 AND net10.0 (main's baseline moved past the dispatch-time 5132 while this branch was in flight; this branch's delta is test-count-neutral); Release --no-restore -warnaserror 0 warnings 0 errors. TEST PROJECT ONLY: no production code change, no locale/model surface, no deploy.

CLOSED 2026-10-04 by the orchestrator after the full cycle: ALREADY-FIXED verdict confirmed (the finding died with JF-717's shared-mint memo, the ordering addendum with the JF-709 fold), merged test-only (worker commits + the main-alignment merge, --no-ff) under the scaled verification (the diff's assert removals are exactly the three converted inline bundles), 5143/5143 both TFMs, no deploy. JF-754 unused.
<!-- SECTION:FINAL_SUMMARY:END -->
