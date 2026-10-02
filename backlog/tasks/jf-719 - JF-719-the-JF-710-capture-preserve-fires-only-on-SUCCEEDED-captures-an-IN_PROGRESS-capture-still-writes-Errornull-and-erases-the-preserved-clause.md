---
id: JF-719
title: >-
  JF-719 - the JF-710 capture preserve fires only on SUCCEEDED captures; an
  IN_PROGRESS capture still writes Error=null and erases the preserved clause
status: Done
assignee: []
created_date: '2026-10-02 15:30'
updated_date: '2026-10-02 23:22'
labels:
  - catalog
  - observability
dependencies:
  - JF-710
references:
  - >-
    backlog/tasks/jf-710 -
    JF-710-the-startup-skill-update-capture-overwrites-the-ledger-Error-null-erasing-the-JF-705-frozen-clause-under-the-skip-gated-restart.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 from the JF-710 implementation round, before the gates: the preserve
gate was implemented exactly per the coordination note (captured entry would be
Status=SUCCEEDED with Error=null), and code-reading during implementation showed that
gate misses a branch that is not rare in the wild.

THE RESIDUAL: the capture runs immediately after `UpdateSkillAsync`, whose
`UpdateInteractionModelsAsync` PUTs all 17 locale models with NO settle-wait after each
PUT (`PutLocaleModelPreservingWiringAsync` settles only the PREVIOUS build, before its
GET). Model builds take ~15-30s while the PUT loop moves on, so the capture's single
`GetSkillStatusAsync` reads IN_PROGRESS (with empty Errors) for at least the
most-recently-PUT locales on a version-change restart. That branch writes
Status=IN_PROGRESS, Error=null: the preserve does not fire (the gate requires
SUCCEEDED), so under the same compound skip-gated-restart path (startup re-sync skipped
via the CatalogSyncTask gates), the JF-705/JF-709 frozen clause and any JF-495 canary
are STILL erased exactly as filed in JF-710; only the settled-build subset is fixed.
JF-709's PreservedOrSkippedStatus doc already names IN_PROGRESS "the startup capture's
in-flight poll world", confirming the branch is real, and the capture-written
IN_PROGRESS row is itself a transient frozen into the ledger until the next writer
(same family as the JF-709 rework F3 clamp rationale).

NOT fixed in JF-710 because the coordination note's preserve spec and the task summary
gate the preserve on SUCCEEDED+null explicitly; extending the gate is a spec decision
for the coordinator, not a worker-side silent superset.

FIX SHAPES to weigh at pick-up:
1. Widen the preserve gate to any clean capture (Error would be null, ANY state): the
   preserve's semantics (the capture's observation says nothing about catalog state)
   do not depend on the build outcome. Cheapest change (the `else if` in
   CaptureLocaleModelStatusesAsync drops its state check); the row then reads
   IN_PROGRESS with the preserved clause, which is strictly better than a wiped row.
2. Also or instead: make the capture settle-wait per locale before reading
   (WaitForLocaleBuildToSettleAsync before the status GET) so the capture stops
   freezing transients at all; costs startup latency (up to the settle budget per
   locale) and is a bigger behavioral change than the observability fix needs.

Pin when landing: an IN_PROGRESS clean capture over a clause-carrying row keeps the
clause (the SUCCEEDED pin already exists in SkillStartupTests
CaptureLocaleModelStatusesAsync_CleanCapture_OverNoPutClauseRow_PreservesClauseAndForeignDropsTail;
add the IN_PROGRESS twin).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The preserve fires on a clean capture regardless of the observed build state (or the chosen equivalent), with the IN_PROGRESS twin pin in SkillStartupTests. DONE: the gate in SkillStartup.CaptureLocaleModelStatusesAsync widened from `state == "SUCCEEDED"` to a SkillStatusState ALLOWLIST (`is SUCCEEDED or IN_PROGRESS`); pins: the IN_PROGRESS twin CaptureLocaleModelStatusesAsync_CleanInProgressCapture_OverNoPutClauseRow_PreservesClauseAndForeignDropsTail (compound row, double-capture idempotence), the widened-gate guard CaptureLocaleModelStatusesAsync_FailedCaptureWithoutErrors_OverClauseRow_ReplacesWholesale (proven load-bearing by counterfactual: with the gate temporarily a bare `else`, exactly this pin fails both TFMs), and CaptureLocaleModelStatusesAsync_InProgressCaptureWithErrors_OverClauseRow_ReplacesWithOwnError.
- [x] #2 dotnet build 0 errors, dotnet test green both TFMs, no new warnings. DONE: `dotnet build Jellyfin.Plugin.AlexaSkill.sln` 0 errors 0 warnings; `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` 4974/4974 net9.0 AND net10.0 (baseline 4971 + 3 pins), exit 0; only the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337.
- [x] #3 The decision (widen vs settle-wait) recorded here with the trade named. DONE: WIDEN, per the coordinator's recorded decision, which the consumer read CONFIRMED rather than contradicted (details below).
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 749df3c1 + gate-marker tail 5fb0ac0d, merged as fdddfac9. The capture preserve's gate widened from SUCCEEDED-only to the SUCCEEDED-or-IN_PROGRESS allowlist (the coordinator's pre-recorded WIDEN decision, confirmed by the consumer read and the gate-marker's DLL reflection: SkillStatusState is exactly IN_PROGRESS|FAILED|SUCCEEDED, TIMEOUT unreachable from the status GET; FAILED clean captures still replace wholesale, counterfactual-pinned; the denylist alternative would have parked clauses on failedModels rows). 3 new pins via the JF-710 fake-status harness. Worker gates green (simplify 2 applied; code-review high 2 applied, 2 filed as JF-722); the closure-gate /simplify round over the tail diff ran 4 angles with three keep-as-committed verdicts and one skip-with-reason (the order-of-magnitude clause kept as the note's core value). Gate-marker verified all five scrutiny axes against primary artifacts; its 3 findings all landed (JF-722's mitigating fact CORRECTED - the PUT loop's settle is a no-op on in-flight builds so ~10-17 of 17 locales read IN_PROGRESS at capture, the frozen-Status symptom is the common case, priority raised to medium; the speculative stale-errors shape folded there; the race note's widened fire set recorded). Suites: worker 4974/4974, orchestrator independent 4974/4974 both TFMs on the final state closing the worker's net9.0 evidence gap directly; merged-tree net10.0 4981/4981, net9.0 4980/4981 with the single miss the DoubleMetaphone wall-clock assertion under three-way machine contention, PROVEN ENVIRONMENTAL by the isolation re-run (20/20 in 65ms). Production surface changed (SkillStartup): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
