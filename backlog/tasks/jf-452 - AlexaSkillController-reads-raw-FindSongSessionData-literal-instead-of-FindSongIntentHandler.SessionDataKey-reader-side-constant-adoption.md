---
id: JF-452
title: >-
  AlexaSkillController reads raw "FindSongSessionData" literal instead of
  FindSongIntentHandler.SessionDataKey (reader-side constant adoption)
status: Done
assignee: []
created_date: '2026-09-02 03:15'
updated_date: '2026-09-02 05:07'
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02 simplify pass EXTENDED SCOPE: two independent reviewers flagged that the constant adoption alone leaves the deeper gap. The harness's Select() mirror (DispatchHarness.cs:148-181) replicates the controller's force-route conditions from a comment citation; NOTHING pins the controller side (a controller-only edit, e.g. dropping the is-IntentRequest guard, the exact 2026-08-21 incident class, leaves the suite green). RegisteredHandlerTypes (DispatchHarness.cs:86) is likewise a verbatim copy of Registrator.cs:110-129. The full fix: extract the selection semantics (force-route predicate + first-CanHandle-wins loop, and optionally the handler-type enumeration as an internal static beside the Registrator) into an internal production unit the controller calls and the harness reuses; the mirrors become calls and JF-452's constant adoption falls out naturally. Optional piggyback noted by the altitude reviewer: resolving harness handlers via a real ServiceCollection provider would also pin DI activation (a handler whose ctor deps Registrator never registers currently constructs fine from mocks but crashes at startup).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Selection semantics now have ONE production owner. HandlerSelector.Select (internal static, Alexa/Handler/) holds the FindSong force-route (matching FindSongIntentHandler.SessionDataKey, constant adopted, IntentRequest-only with the 2026-08-21 rationale) plus the first-CanHandle-wins loop; AlexaSkillController delegates; Registrator.RegisteredHandlerTypes() is the shared handler discovery; the test harness Select/RegisteredHandlerTypes are delegations, so all 13 DispatchRoutingTests assertions (unchanged) now exercise the real production path. The controller's per-instance _findSongHandler memo removed (write-read-once dead weight). Simplify follow-up: SimulatorController's hand-rolled loop (the last copy) also delegates now (behavior-identical: synthetic requests never carry FindSongSessionData). Gates: implementer /simplify 4 angles clean (one deliberate skip documented); orchestrator code-review high CLEAN on all six checks incl. an empirical scratch probe of the CA1851 suppression; orchestrator /simplify applied the simulator fold + refuted two false-positive findings with evidence (CA1851 pragma is load-bearing - a fresh full rebuild fires it at both enumeration sites; the Request.Type using is load-bearing for the Request base class). Suite 2858/0, Release 0 warnings. Commit 32c9be5.
<!-- SECTION:FINAL_SUMMARY:END -->
