---
id: JF-898
title: >-
  Rejected-climb fresh launches of Audio-typed remapped chapters still classify
  VideoAppAudio mid-play: the launch-verdict mint covers only accepted-climb
  arms
status: To Do
assignee: []
created_date: '2026-10-16 10:39'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual from the JF-894 round (worker handback, 2026-10-16): the REJECTED-climb fresh-launch shape of an Audio-typed remapped chapter (shared container of sibling books / dangling ParentId, the JF-793 rejection cases) still records bare LaunchRoute VideoApp in the ledger, so mid-play it classifies VideoAppAudio and voice pause speaks the music line over a book. The JF-894 fix covers only the ACCEPTED-climb arms (the launch sites record VideoAppAudiobook when the verified climb accepted). Out of that task's scope by design; same family as the JF-799 residuals. FIX SHAPE when taken: the rejected-climb launch path records nothing book-like today; either record a distinct route value at the rejected arm (the launch KNOWS the caller intended a book even when the climb failed, the caller-intent doctrine) or accept and document the corner as unreachable for real libraries (the census shape's collapsed single-file books). Decide with the JF-799 residual family in one pass.
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
