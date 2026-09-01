---
id: JF-433
title: >-
  Test harness for CanHandle/controller dispatch: routing regressions are
  invisible to the handler-level suite
status: To Do
assignee: []
created_date: '2026-09-01 06:06'
labels:
  - test-infrastructure
  - routing
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:428'
  - Jellyfin.Plugin.AlexaSkill/Alexa/Pipeline/RequestPipeline.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Registrator.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Repo-known test gap, most recently documented as a JF-419.2 residual (review round: 'unit tests bypass CanHandle + controller routing') and the subject of a standing memory note (feedback_handler_tests_miss_routing: FallbackIntentHandler was dead code for months with a green suite because tests call HandleAsync directly). Never filed as work. With the warming gates now spanning 10 handlers and FindSong's controller force-route (AlexaSkillController routes IntentRequests with FindSongSessionData to FindSongIntentHandler - the InvalidCastException incident of 2026-08-21 was exactly this layer), the dispatch layer deserves systematic coverage rather than per-incident tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A test-harness level exists that drives requests through CanHandle + the controller/pipeline dispatch (not HandleAsync directly), usable for routing-sensitive regressions
- [ ] #2 At least the JF-419.2 incident shape is covered: a warming-gated intent request dispatches through the real routing and produces the warming Tell (the real-handler pipeline test in SkillWarmingUpTests already covers pipeline+handler; this adds the CanHandle/dispatch layer)
- [ ] #3 The dead-code class of regression (FallbackIntentHandler precedent: dead for months with a green suite) becomes detectable: a test asserts every registered handler's CanHandle fires for its intent
<!-- AC:END -->

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
