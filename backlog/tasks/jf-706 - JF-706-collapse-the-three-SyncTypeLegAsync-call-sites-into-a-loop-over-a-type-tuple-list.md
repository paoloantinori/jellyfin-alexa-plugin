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
