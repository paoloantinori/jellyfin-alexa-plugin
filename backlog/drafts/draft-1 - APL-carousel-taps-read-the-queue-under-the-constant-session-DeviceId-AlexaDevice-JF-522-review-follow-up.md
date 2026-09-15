---
id: DRAFT-1
title: >-
  APL carousel taps read the queue under the constant session DeviceId
  "AlexaDevice" (JF-522 review follow-up)
status: Draft
assignee: []
created_date: '2026-09-15 01:54'
labels:
  - bug
  - follow-up
  - resume
dependencies: []
references:
  - JF-522
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-522 code review (2026-09-15): AplUserEventHandler.GetResumeOffset resolves its per-device queue via `session.DeviceId`, but every plugin-authenticated Jellyfin session carries the CONSTANT DeviceId "AlexaDevice" (AlexaSkillController.cs:210), not the Echo's device id that keys DeviceQueueManager. The ItemPositionState first-choice lookup therefore always misses and every APL carousel tap falls through to the UserData fallback (base-0 items: identical value, so no live bug; per-device position state is simply dead on this path). Fix: thread the Alexa device id from the request context into GetResumeOffset. Same class as the ReportPlaybackProgress device-key bug fixed in JF-522.
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
