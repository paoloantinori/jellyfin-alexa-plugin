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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
