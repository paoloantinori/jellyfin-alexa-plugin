---
id: JF-502
title: >-
  JF-495 poll bug: 404 on a consumed SMAPI update-request URL logs an ERR every
  ~90s during playback (should read as terminal-completed); find what calls
  catalog SMAPI during playback
status: To Do
assignee: []
created_date: '2026-09-06 08:47'
labels:
  - smapi
  - bug
  - catalog
dependencies: []
references:
  - JF-495
  - 'podman logs 2026-09-06 10:43-10:46'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Bug in the JF-495 hardening observed live 2026-09-06 (10:43-10:46, during episode playback): CatalogManager logs 'SMAPI request failed: 404 Not Found' with an HTML body roughly every 90 seconds. The update-request Location URL used by PollSmapiOperationAsync appears to be consumed/expired after the build completes (or is one-shot), so a poll that outlives it errors instead of reading as terminal. Fix: in PollSmapiOperationAsync (and the model-build settle wait), treat 404 on the update-request URL as TERMINAL-COMPLETED (or at minimum downgrade to a debug log with a terminal disposition); also identify WHAT issues catalog-manager SMAPI calls every ~90s during playback (dynamic entities? a periodic refresh?) since no catalog sync was scheduled in that window, and make sure path does not hammer SMAPI. Evidence: podman logs 10:43:48, 10:45:08, 10:46:29 2026-09-06.
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
