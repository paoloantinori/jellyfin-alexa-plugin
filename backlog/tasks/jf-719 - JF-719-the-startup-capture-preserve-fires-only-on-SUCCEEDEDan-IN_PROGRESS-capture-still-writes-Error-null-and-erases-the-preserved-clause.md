---
id: JF-719
title: >-
  JF-719 - the JF-710 capture preserve fires only on SUCCEEDED captures; an
  IN_PROGRESS capture still writes Error=null and erases the preserved clause
status: To Do
assignee: []
created_date: '2026-10-02 15:30'
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
- [x] The preserve fires on a clean capture regardless of the observed build state (or the chosen equivalent), with the IN_PROGRESS twin pin in SkillStartupTests. DONE: the gate in SkillStartup.CaptureLocaleModelStatusesAsync widened from `state == "SUCCEEDED"` to a SkillStatusState ALLOWLIST (`is SUCCEEDED or IN_PROGRESS`); pins: the IN_PROGRESS twin CaptureLocaleModelStatusesAsync_CleanInProgressCapture_OverNoPutClauseRow_PreservesClauseAndForeignDropsTail (compound row, double-capture idempotence), the widened-gate guard CaptureLocaleModelStatusesAsync_FailedCaptureWithoutErrors_OverClauseRow_ReplacesWholesale (proven load-bearing by counterfactual: with the gate temporarily a bare `else`, exactly this pin fails both TFMs), and CaptureLocaleModelStatusesAsync_InProgressCaptureWithErrors_OverClauseRow_ReplacesWithOwnError.
- [x] dotnet build 0 errors, dotnet test green both TFMs, no new warnings. DONE: `dotnet build Jellyfin.Plugin.AlexaSkill.sln` 0 errors 0 warnings; `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` 4974/4974 net9.0 AND net10.0 (baseline 4971 + 3 pins), exit 0; only the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337.
- [x] The decision (widen vs settle-wait) recorded here with the trade named. DONE: WIDEN, per the coordinator's recorded decision, which the consumer read CONFIRMED rather than contradicted (details below).
<!-- DOD:END -->

## Decision record

WIDEN, not settle-wait. The consumer read found: DiagnosticsController counts only
FAILED/TIMEOUT rows in failedModels and documents IN_PROGRESS as healthy, so an
IN_PROGRESS capture-row carries no failure weight and a preserved clause on it is strictly
better than the wiped row; the settle-wait alternative (WaitForLocaleBuildToSettleAsync
per locale before the status GET) would add up to the settle budget per locale of startup
latency to protect a sub-case the content-keyed preserve already handles safely. The gate
is an ALLOWLIST (SUCCEEDED or IN_PROGRESS), not a FAILED/TIMEOUT denylist, so a state the
SkillStatusState enum ever grows into fails safe (replace wholesale); a reflection dump
of the referenced Alexa.NET.Management (5.10.0) proved the enum is exactly
IN_PROGRESS|FAILED|SUCCEEDED, so TIMEOUT, the ledger's other failure vocabulary, is
unreachable from the capture's status GET (TIMEOUT rows come from the sync writers' own
poll outcome). A FAILED observation with a NO-errors array still replaces wholesale
(pinned): the naive "any no-errors capture preserves" widening would park the catalog
clause on a row the panel counts in failedModels. An IN_PROGRESS capture WITH build
errors still replaces wholesale (the Errors branch is first and state-independent;
pinned).

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The JF-710 preserve no longer depends on the observed build having settled. GATE SHAPE:
the capture's preserve branch condition widened from `state == "SUCCEEDED"` (Ordinal
string) to a SkillStatusState ALLOWLIST, `localeStatus.LastModified.Status is
SkillStatusState.SUCCEEDED or SkillStatusState.IN_PROGRESS`, so a clean IN_PROGRESS
capture (the freshly-PUT locale the no-settle-wait capture legitimately reads right
after UpdateSkillAsync) preserves the frozen clause and foreign diagnostics exactly like
a settled one. CONSUMER-READ CONFIRMATION (adopted the coordinator's widen decision,
uncontradicted): the diagnostics panel counts only FAILED/TIMEOUT in failedModels and
documents IN_PROGRESS as healthy, so the widened world parks the clause on a row with no
failure weight; PreserveLedgerErrorAcrossCapture was already state-independent
(content-keyed), so the gate is the only owner of the decision and stays at the capture
caller (simplify altitude agent: correct depth, allowlist beats both the
FAILED/TIMEOUT denylist and the task file's drop-the-check-entirely shape). A
reflection dump of the referenced Alexa.NET.Management proved SkillStatusState is
exactly IN_PROGRESS|FAILED|SUCCEEDED, so TIMEOUT is unreachable from the status GET and
an allowlist fails safe on any enum growth. PINS (3 new, SkillStartupTests, the
JF-710 FakeStatusSmapiManagement harness, CaptureCleanAsync parameterized by state):
the IN_PROGRESS twin over the compound clause+tail+trailed-foreign row (clause +
unframed foreign survive, tail drops, double-capture idempotence), the FAILED-with-NO-
errors wholesale replace (the widened gate's guard; proven load-bearing by
counterfactual: gate temporarily a bare else fails exactly this pin both TFMs), and the
IN_PROGRESS-with-errors wholesale replace (Errors branch is state-independent). The
preserve arm gained the branch-decision LogDebug the logging policy asks for (this
subsystem is triaged from ledger forensics). GATES: Skill simplify (4 agents; 2
applied: the method-doc rationale overlap trimmed, the LibrarySyncService doc reflow;
3 skipped with reasons: CaptureCleanAsync(FAILED) naming oxymoron, Theory conversion
fights the Fact-per-pin convention and the task's twin prescription, enum-read hoisting
below threshold) + Skill code-review high (4 findings: 2 applied, the LogDebug and the
"widened to include IN_PROGRESS" one-word doc fix; 2 FILED as JF-722, the frozen
IN_PROGRESS Status half with no later refresh on ordinary restarts, and the per-locale
null isolation gap; both pre-existing shapes outside the recorded decision, not gate
defects; the review verified the gate itself bug-free). SUITES: 4974/4974 net9.0 and
net10.0 (-m:1, exit 0; baseline 4971 + 3 pins), build 0 errors 0 warnings beyond the
pre-existing xUnit1030 pair. No deploy; do not push.
<!-- SECTION:FINAL_SUMMARY:END -->
