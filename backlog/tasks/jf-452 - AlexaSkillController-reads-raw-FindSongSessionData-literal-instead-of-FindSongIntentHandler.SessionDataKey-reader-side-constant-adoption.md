---
id: JF-452
title: >-
  AlexaSkillController reads raw "FindSongSessionData" literal instead of
  FindSongIntentHandler.SessionDataKey (reader-side constant adoption)
status: To Do
assignee: []
created_date: '2026-09-02 03:15'
labels:
  - tech-debt
  - consolidation
dependencies: []
references:
  - Controller/AlexaSkillController.cs
  - Alexa/Handler/Intent/FindSongIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-430 review sweep (2026-09-02). Controller/AlexaSkillController.cs:414 (the FindSong force-route check) reads the raw string literal "FindSongSessionData" instead of FindSongIntentHandler.SessionDataKey. The writer side (FindSongIntentHandler) uses the constant; the reader side uses a literal copy. JF-433's DispatchRoutingTests.FindSongSessionKey_MatchesControllerLiteral already pins literal == constant so drift is now caught by the suite, but the clean fix is for the controller to reference the constant directly (promote SessionDataKey to a shared/internal const if needed) and keep the harness pin as the wire-format guard. One-line production change plus constant promotion; zero behavior change.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
