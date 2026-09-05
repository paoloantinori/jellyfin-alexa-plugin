---
id: JF-497
title: >-
  Sync canary never fires during full 12-locale bursts (per-locale poll budget
  vs serialized build queue): add a post-sync canary sweep or scale the budget
status: To Do
assignee: []
created_date: '2026-09-05 16:44'
labels:
  - smapi
  - catalog-sync
  - observability
dependencies: []
references:
  - JF-495
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live observation from the JF-495 post-deploy verification (2026-09-05 18:32-18:43): the hardened catalog sync serializes per-locale build waits correctly (the H1 GET-race is gone) and all 12 builds eventually SUCCEEDED with correct content, but during a FULL 12-locale sync burst every locale's own build settles AFTER its ~80s poll budget expires (SMAPI serializes builds per skill: locale N waits for N-1 predecessors), so every locale logs 'did not settle within the poll budget' (buildStatus TIMEOUT) and the post-deploy CANARY never fires during bursts. The canary currently only fires for single-locale operations (rebuild endpoint, custom deploys). Options: scale the per-locale poll budget by remaining queue position, or run a post-SYNC canary sweep (after the last locale's PUT, GET-back all locales once the queue drains) instead of per-locale mid-sync. Not urgent: content correctness is preserved by the serialization; this is about making the canary actually observe burst deployments.
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
