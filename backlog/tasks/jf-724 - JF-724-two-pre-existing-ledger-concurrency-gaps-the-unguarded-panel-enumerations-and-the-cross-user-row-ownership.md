---
id: JF-724
title: >-
  JF-724 - two pre-existing locale-ledger concurrency gaps surfaced by the JF-722
  gates: the admin panel's unguarded ledger enumerations can 500 on a concurrent
  writer, and the ledger's global-by-locale rows are owned by no writer when two
  SMAPI users are linked
status: To Do
assignee: []
created_date: '2026-10-03 02:35'
labels:
  - observability
  - catalog
  - reliability
dependencies:
  - JF-722
references:
  - >-
    backlog/tasks/jf-722 -
    JF-722-two-startup-capture-residuals-the-frozen-IN_PROGRESS-Status-half-and-the-per-locale-null-isolation.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-722 /code-review round (effort high), carrying
the two findings that round judged real but PRE-EXISTING (not introduced by JF-722's
diff, which only widened the exposure windows):

1. UNGUARDED PANEL ENUMERATIONS (code-review F1's tail): the per-locale status ledger
   (PluginConfiguration.LocaleModelStatuses, a Collection) is written by four writers
   (the JF-705/JF-709 sync legs, the JF-710 startup capture, JF-722's deferred
   refresh) with NO synchronization, and the WRITERS each learned to guard their own
   reads (the JF-709 read guard, the capture's per-locale catch, the refresh's
   per-poll/per-locale catches) - but the READ-ONLY consumers never did.
   DiagnosticsController.GetPanel enumerates the collection three times
   (Max(e => e.LastUpdated), Count/Count(...) in failedModels, Any(...) in
   ModelsDeployed) and ConfigurationController.GetCustomModelStatus LINQ-projects it;
   a concurrent SetLocaleModelStatus (a List indexer-set or Add bumps the version)
   colliding with any of those enumerations throws InvalidOperationException and 500s
   the admin surface. Realistic window: the JF-722 refresh now writes rows up to
   ~3 minutes after every version-bump restart while the startup-triggered
   CatalogSyncTask may run its legs concurrently. FIX SHAPE: a snapshot copy under a
   try/catch in the two controllers (the JF-709 degrade shape), or a reader-writer
   lock owned by the ledger accessors; the snapshot is the small fix.
2. CROSS-USER ROW OWNERSHIP (simplify altitude round's closing note): the ledger is
   keyed by LOCALE ONLY while the observation sources are PER-USER skills, so with two
   linked SMAPI users one user's capture (and now refresh) rewrites the shared rows
   from ITS skill's status, last-writer-wins. The JF-710 capture already had this
   shape (its capture writes all served locales unconditionally); JF-722's refresh
   inherits it (its family filter is content-keyed, not user-keyed). Not user-visible
   with one user (the deployed household today) and cosmetic at worst with two
   (both skills deploy the same embedded models, so the settled truths agree); a
   per-user key or a writer-priority rule is the fix if multi-user ever matters.
   Filed so the ownership model is a recorded decision rather than an accident.

REWORK-ROUND APPENDS (2026-10-03, JF-722 gate-marker round, findings 4/5/6):

3. MULTIPLIED RACE EXPOSURE + DIAGNOSTIC-LOSS VICTIM (rework F4, sharpening the
   JF-722 code-review F1 tail): the JF-722 deferred refresh multiplies the KNOWN
   RACE's unguarded read-modify-write up to 4 polls x 17 locales across its
   ~3-minute budget, overlapping the post-restart CatalogSyncTask window, and the
   clobber victim is not only the capture-side row: a sync-authored SETTLED row
   carrying a real canary diagnostic that lands between a refresh pass's family
   read and its SetLocaleModelStatus is overwritten with the STALE read's Error
   (Source and carried text from the pre-write row), losing the diagnostic until
   the next sync run. The capture's preserve-gate comment and the rewrite pass's
   doc carry the sizing; the structural fix belongs with item 1's synchronization
   (a lock owned by the ledger accessors closes both).
4. FAMILY-MEMBERSHIP PIN GAP (rework F5): the refresh's family predicate keys on
   Source == "Embedded", whose DTO default (LocaleModelStatus.Source initializer
   and LocaleModelStatusEntry.Source) is ALSO "Embedded", so any future ledger
   writer that composes a row without setting Source explicitly silently joins the
   capture family and gets its rows rewritten by the refresh. The JF-722
   CaptureRefreshPairingTests roster pins CALL wiring (who calls the capture /
   scheduler), not ROW CONTENT (who writes family-eligible rows); a
   row-content roster would scan for SetLocaleModelStatus call sites and pin the
   writer set against {capture, refresh, sync writers' shell}.
5. CROSS-USER PRE-CHECK INTERPLAY (rework F6, item 2 made concrete): the refresh's
   no-frozen-rows pre-check scans the GLOBAL ledger while its observations are
   per-skill, so user A's refresh stays alive on user B's frozen IN_PROGRESS rows
   and settles them from A's skill's status (and vice versa); with both users'
   skills mid-deploy the two refreshes also keep each other's budgets alive. Same
   ownership model as item 2; listed separately because the pre-check (not just
   the rewrite) is a concrete cross-user read-decides-for-other path.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the worker in one pass (no rework round demanded). THE ONE DESIGN, all five items on it:

1. UNGUARDED PANEL ENUMERATIONS: FIXED via a private ledger lock owned by PluginConfiguration's accessor family (Get/Set/Snapshot/Update + SaveUnderLedgerLock). DiagnosticsController.GetPanel and ConfigurationController.GetCustomModelStatus now derive every answer from one GetLocaleModelStatusSnapshot copy (Max/Count/Any/ToDictionary over the stable array); red-check proven twice (reverting GetPanel to the live collection throws InvalidOperationException from Enumerable.Max inside the stress, at both the original and the ~3x-cheaper final budget, on both TFMs), and the discipline is machine-enforced by LocaleLedgerAccessorRosterTests (the CaptureRefreshPairingTests IL-scan idiom: no method outside PluginConfiguration may touch the collection property).
2. CROSS-USER ROW OWNERSHIP: DECIDED AND DOCUMENTED, with the concrete path scope-fixed. The ledger stays locale-keyed and global (the panel's product surface is one row per locale; identical embedded models mean settled truths agree), the decision recorded on the collection's OWNERSHIP MODEL doc with the code-review-corrected honest bound (a divergent household shows the last writer's truth, panel weight included, until the next sync); the fix for the read-decides-for-other path is row authorship: the observation family stamps the new additive ObservedSkillId (XML-additive, pre-JF-724 rows read null, rollback-safe per the JF-721 pattern) and ships it in the status JSON as the forensics surface.
3. MULTIPLIED RACE EXPOSURE: FIXED at the shared path the item named. ASSESSMENT FIRST: JF-721 did not materially change the window (marker-string composition vs field copy, pure in-memory either way; the multiplier was JF-722's 4 polls x 17 locales). The lock therefore belongs on the accessors, and the new atomic UpdateLocaleModelStatus(locale, compose) closes the whole read-modify-write class in ONE lock acquisition: all four writers route through it (the capture preserve, the refresh settle with its null-decline family check, the sync PUT path via the shared WriteLedgerEntry shell, and the no-PUT writer whose read-decide-carry moved inside the compose, deleting its private JF-709 read guard). The code-review round then caught the one enumeration the lock did not cover: SaveConfiguration itself serializes the live collection, so every ledger writer saves through the new SaveUnderLedgerLock, and the capture's save failure is non-fatal without flipping its return (a false return would have flipped the paired refresh into recapture mode after rows were written). Residual, documented once on the lock doc: the Jellyfin admin-save config-object swap (pre-existing, not lock-fixable across objects) and human-paced admin saves outside the wrapper.
4. FAMILY-MEMBERSHIP PIN GAP: the caveat fields do NOT discriminate family membership (a clean capture row carries Caveat=None; nothing in the caveat space marks capture authorship), so the gap stood; closed STRUCTURALLY by flipping the record's Source default from "Embedded" to empty, so any writer that omits Source lands outside the Ordinal family key (pinned), while the XML entry's "Embedded" default stays as the load-bearing pre-Source-era compat (the deliberate asymmetry pinned with its rationale). The roster test adds the writer-side tripwire the filing sketched (every collection touch must be an accessor).
5. CROSS-USER PRE-CHECK INTERPLAY: FIXED by the item-2 attribution: HasInProgressCaptureRows is now skill-scoped (predicate = IN_PROGRESS + Embedded + ObservedSkillId null-or-mine), so user A's refresh neither triggers on, stays alive on, nor settles user B's frozen rows; legacy null rows stay eligible for any refresh and become attributed on settle. Pins: capture stamps + last-writer-wins between captures, other-skill row exits the pre-check with zero network calls, a mixed ledger settles only own rows byte-identically, unattributed rows stay eligible and become attributed.

Gates: /simplify 4-agent round (11 applied, 2 skipped with reasons, recorded on DoD #9); /code-review high 6 findings all applied in-diff (F1 save-under-lock + capture return honesty, F2 snapshot stability doc, F3 ownership honest bound, F4 roster rule scoping, F5 real stress oracle + yield, F6 GetLocaleModelStatus door doc), JF-741 UNUSED (nothing real-but-out-of-scope). Suites: 5103/5103 both TFMs on the exact final state, Release -warnaserror clean. Production surface changed (PluginConfiguration, SkillStartup, LibrarySyncService, both controllers): NOT deployed (held for the orchestrator's batch decision).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Debug both TFMs and Release -warnaserror clean on the whole solution, final state)
- [x] #2 dotnet test passes (5103/5103 net9.0 and 5103/5103 net10.0, -m:1, final state; baseline 5093 + 10 new: 5 accessor/DTO/stress pins in LocaleModelStatusCaveatTests, 4 cross-user capture/refresh pins in SkillStartupTests, 1 IL roster in LocaleLedgerAccessorRosterTests)
- [x] #3 No new compiler warnings introduced (0 warnings on the clean rebuild of both projects; the two xUnit analyzer warnings the first draft raised, xUnit2013 and xUnit1030, fixed before the final state)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched; the ledger rows are the XmlSerializer LocaleModelStatusEntry twin, extended additively)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no intent or handler logic; the new behavioral pins are the red-checked concurrency stress over both admin surfaces and the cross-user capture/refresh family pins)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings; the one new endpoint field observedSkillId is additive JSON the config page ignores)
- [x] #9 /simplify passed (4-agent round, all applied: snapshot ToArray under the lock; SetLocaleModelStatus delegates to UpdateLocaleModelStatus so the lock/upsert plumbing has one site; the refresh compose's dead failure-weight arm deleted with its comment folded onto the fall-through; the capture's stale per-locale catch comment reworded to the atomic-update reality; the enumeration rationale single-homed on the ledger's lock doc with one-line pointers at the two controllers and the capture's inline block trimmed; the pre-check's JF-709 degrade catch DROPPED with the locked snapshot closing its throw source and the worker's own non-fatal catch noted as the remaining degrade; the RefreshSkillId const made structural (the helper's actual argument plus the direct worker call); the family predicate moved to entry/primitive fields so the scan allocates no records per row; the refresh's settle warning moved after the atomic write so it fires family-only-once as before; the stress budget cut ~3x (60k->20k writes, 300->150 locales) with the RED-CHECK re-verified at the smaller budget; WriteLedgerEntry's contract sentence corrected for the PUT path's ignore-current shape. SKIPPED with reasons: observedSkillId in the status JSON has no renderer, kept as the consciously-accepted ledger-forensics surface mirroring the existing source field; the full-file consolidation of every capture-path "amzn1.ask.skill.test-id" literal left alone, independent test acts, the const covers every refresh-path site where the pairing claim lives)
- [x] #10 /code-review high passed (6 findings, ALL 6 APPLIED, none filed: F1 SaveConfiguration serializes the LIVE collection outside the lock and can throw inside a save racing a writer's Add, so every ledger writer now saves through the new PluginConfiguration.SaveUnderLedgerLock and the capture's save failure is non-fatal WITHOUT flipping its return (the false return would have flipped the paired refresh into recapture mode after rows WERE written); F2 the snapshot's stability claim now states the reference-shallow copy and the replace-only entry discipline it rests on (structural immutability impossible: XmlSerializer needs settable entry properties, and records lack the Locale key the status endpoint needs); F3 the ownership doc's "cosmetic" claim corrected with the honest divergent-household bound (a throttled user B's FAILED overwrites carry panel consumer weight; ObservedSkillId makes it diagnosable, a per-user key remains the fix if multi-user matters); F4 the roster test's rule scoped honestly (plugin-assembly-only; tests and Jellyfin's serializer sit outside, writers' saves serialized via the F1 wrapper); F5 the stress readers' assertions were vacuous NotNull(ActionResult) and unthrottled, now IsType<JsonResult> + NotNull(Value) + a 1ms yield, red-check re-verified at the final shape; F6 GetLocaleModelStatus documented test-only/door-status like its Set twin now that no production reader calls it. JF-741 stays UNUSED: nothing real-but-out-of-scope emerged, every finding landed in this diff)
<!-- DOD:END -->
