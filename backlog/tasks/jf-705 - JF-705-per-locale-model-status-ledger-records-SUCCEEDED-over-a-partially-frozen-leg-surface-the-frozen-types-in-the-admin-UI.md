---
id: JF-705
title: >-
  JF-705 - per-locale model-status ledger records SUCCEEDED over a partially
  frozen leg; surface the frozen types in the admin UI
status: To Do
assignee: []
created_date: '2026-10-02 10:12'
labels:
  - catalog
  - observability
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
(commit 1a48a53d, finding 1 of 6). The JF-695 isolation is honest at the RUN level
(SyncResult.FrozenTypes gates Success to false; run-level LogError names the frozen
types) but the PER-LOCALE model-status ledger still records a clean SUCCEEDED for a leg
whose model PUT succeeded while one of its catalog types froze: `RunLegAsync` calls
`RecordModelUpdateInLedger(locale, modelUpdate)` (LibrarySyncService.cs:252) whenever at
least one type minted a version, and the entry carries no frozen-type awareness. Failure
scenario: artist freezes in every locale while album/series mint; every leg's model PUT
succeeds and stamps Status=SUCCEEDED (the JF-695 isolation pin itself asserts
ledger.Status == "SUCCEEDED"), so an admin diagnosing "why is artist recognition stale"
sees all locales SUCCEEDED in the config UI and finds the freeze only by grepping server
logs. Note the all-types-frozen shape already stays OUT of the ledger (no version minted
means no PUT and no entry), so the gap is exactly the partial-freeze shape.

THE WORK: thread the leg's frozen types into `RecordModelUpdateInLedger` and surface them
in whatever field the config UI (config.html) renders for each locale entry. Before
choosing the shape, READ the ledger consumer: find where the per-locale status strings
are parsed/rendered (Configuration/ and config.html) and decide between appending a named
clause to an existing message/error field versus a distinct status value the UI already
tolerates. Add a pin: partial freeze with successful PUT must surface the frozen type in
the ledger entry. Keep the run-level honesty (Success gate) unchanged.
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
