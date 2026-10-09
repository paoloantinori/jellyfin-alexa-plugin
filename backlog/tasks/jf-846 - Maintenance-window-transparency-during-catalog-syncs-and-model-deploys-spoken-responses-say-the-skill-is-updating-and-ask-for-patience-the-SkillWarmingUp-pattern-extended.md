---
id: JF-846
title: >-
  Maintenance-window transparency: during catalog syncs and model deploys,
  spoken responses say the skill is updating and ask for patience (the
  SkillWarmingUp pattern extended)
status: To Do
assignee: []
created_date: '2026-10-09 11:54'
labels:
  - ux
  - resilience
  - pipeline
milestone: m-18
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the maintainer's request during a live catalog-sync window ("when the skill is self-maintaining, invocations should say so and ask for patience instead of leaving users thinking it's broken").

DESIGN (agreed with the maintainer):
- A static maintenance scope (e.g. CatalogSyncScope/MaintenanceMode) opened by the catalog-sync task at start and closed at end, and by the custom-model rebuild endpoint during its PUTs. No persisted state.
- A RequestPipeline interceptor (or the existing warming-gate plumbing, the SkillWarmingUp precedent JF-419) checks the scope: while open, spoken responses gain a SHORT prefix ("Sto aggiornando la skill, un attimo di pazienza..." per locale) and library-heavy requests that would collide with the in-flight model rebuilds can take the warming-style Tell.
- Honest scope note: during a sync, voice requests mostly WORK (Amazon serves the previous model until the new build completes; Jellyfin queries don't contend with the sync). The prefix informs without blocking. The container-restart window during DLL deploys (~45s, server down, device shows the platform error) is NOT coverable server-side and is excluded.

DELIVERABLES: the scope type + open/close at the sync task and the rebuild endpoint; the interceptor branch; the locale key(s) in all 17 locales + the AllExpectedKeys ledger; unit pins (scope open prefixes, scope closed does not, the warming-style fallback shape if taken); the warming-gate coverage test updated if a new gated path is added. Consider ALSO logging the window start/end at Information so triage can correlate user complaints with maintenance windows (the debug-logging policy).
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
