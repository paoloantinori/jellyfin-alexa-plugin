---
id: JF-433
title: >-
  Test harness for CanHandle/controller dispatch: routing regressions are
  invisible to the handler-level suite
status: In Progress
assignee:
  - zai
created_date: '2026-09-01 06:06'
updated_date: '2026-09-02 03:11'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Harness + tests delivered (subagent, 2026-09-02):

- Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchHarness.cs: reflection-driven construction of ALL BaseHandler subclasses in the exact Registrator order (same filter + FallbackIntentHandler-last + ThenBy(Name)); Select() mirrors the controller's force-route + first-CanHandle-wins loop; DispatchAsync() executes through the real RequestPipeline (empty interceptor lists; the warming translation lives in the pipeline itself so it is exercised); OverrideDependency&lt;T&gt;() for dependency swaps (e.g. warming index); EnableExecution() + CreateExecutionContext() wire user/session/Plugin.Instance for execution tests. Zero production-code changes.
- Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchRoutingTests.cs: 14 tests covering AC#1-#3 plus the real-Registrator descriptor mirror (runs Registrator.RegisterServices against a real ServiceCollection and compares registrations to the harness order), the FindSongSessionData literal/constant sync pin, the hardware Play-button CanHandle tie (PlayIntentHandler beats ResumeIntentHandler by alphabetical order), model-vs-handler bidirectional cross-checks with staleness-guarded allowlists, and the SessionEnded+FindSong fallthrough (2026-08-21 incident shape).

Findings surfaced by the harness on first run, both filed: JF-450 (LoopAllOffIntent/LoopAllOnIntent/RepeatSingleOnIntent declared in de-DE/fr-FR/fr-CA/it-IT models with no claiming handler; runtime CouldNotUnderstand) and JF-451 (RepeatIntentHandler claims AMAZON.RepeatIntent and SetReminderIntentHandler claims SetReminderIntent, both declared in zero models: unreachable handlers; PlayIntentHandler's intent-name branch is model-dead but the handler stays alive via the hardware Play button).

Verification: dotnet test full suite 2857 passed / 0 failed (baseline 2841 + 14 new + 2 from concurrent work in the tree). /simplify run via 4 parallel reviewers; findings applied (dead members removed, real-Registrator mirror added, literal-vs-constant pin, allowlist staleness guards, locale-carrying failure messages, probe dedup). Code-review skill intentionally not run per the task brief.
<!-- SECTION:NOTES:END -->

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
