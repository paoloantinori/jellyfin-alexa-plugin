---
id: JF-704
title: >-
  JF-704 - a throwing plantWarmCache strands the parked endpoint task
  unobserved (background encode mutates static registries after teardown)
status: To Do
assignee: []
created_date: '2026-10-02 11:20'
labels:
  - test-infrastructure
  - tech-debt
dependencies:
  - JF-700
references:
  - >-
    backlog/tasks/jf-700 -
    JF-700-deterministic-in-lock-attribution-for-the-JF-678-vanish-breach-pin-family-pass-controller-to-ServeInLockWarmCacheAsync.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-700 merge
(commit a543785b, finding 3 of 3; PRE-EXISTING lines inside the helper JF-700 touched,
not introduced by it). In `ServeInLockWarmCacheAsync`
(Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs ~1187), if
`plantWarmCache()` throws (e.g. a File.WriteAllText IO failure), the helper propagates
the plant failure honestly but the parked `endpointTask` keeps running unobserved: it
later acquires the released gate, enters the in-lock scope, and starts a real encode
against the recording fake ffmpeg with a disposed logger factory, after the test method
and its `using`-scoped fixtures have exited. Its eventual exception becomes an
UnobservedTaskException candidate and its `SetEncodeActive`/`EncodeGenerationCount`
static writes land after teardown (cross-test contamination under arbitrary xUnit
ordering). The park-assert path already avoids the strand by reading
`endpointTask.Exception` (marking it observed); the plant-throw path is the asymmetric
one.

THE WORK: observe or await the endpoint task on the plant-throw path (a try/catch
around plantWarmCache that observes/awaits endpointTask before rethrowing, or
ContinueWith-only-observation if awaiting would change park timing). Mind the 400ms
park expectation: the observation must not extend the helper's timing budget or change
when the endpoint settles for the non-throwing paths. Add a pin: a plant that throws
fails the test AND leaves no unobserved fault behind (assertable via the observed
exception, or by proving the encode generation count is clean after the failure).
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
