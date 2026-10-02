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
- [ ] The preserve fires on a clean capture regardless of the observed build state (or the chosen equivalent), with the IN_PROGRESS twin pin in SkillStartupTests
- [ ] dotnet build 0 errors, dotnet test green both TFMs, no new warnings
- [ ] The decision (widen vs settle-wait) recorded here with the trade named
<!-- DOD:END -->
