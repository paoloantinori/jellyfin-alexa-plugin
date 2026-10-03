---
id: JF-704
title: >-
  JF-704 - a throwing plantWarmCache strands the parked endpoint task
  unobserved (background encode mutates static registries after teardown)
status: Done
assignee: []
created_date: '2026-10-02 11:20'
updated_date: '2026-10-03 08:03'
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
- [x] #1 dotnet build passes with 0 errors (both TFMs, dotnet build Jellyfin.Plugin.AlexaSkill.Tests, 0 Errors on net9.0 and net10.0)
- [x] #2 dotnet test passes (FINAL state: 5008/5008 net9.0 AND net10.0, exit 0, dotnet test -m:1, no --no-build; baseline 5007 + the 1 new pin; an intermediate pre-simplify-round run also 5008/5008 both TFMs, and the VideoAudioControllerTests class 265/265 both TFMs)
- [x] #3 No new compiler warnings introduced (the only build warnings are the documented pre-existing xUnit1030 pair: the UNTOUCHED JF-681 pin StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock's ConfigureAwait at HEAD line 1337, shifted to 1488 by the diff; same pair, same count in every build, proven against git show HEAD)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: test-only change, no session attributes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-infrastructure change; the new pin ServeInLockWarmCacheHelper_ThrowingPlant_ObservesStrandedEndpointBeforeRethrow is the added coverage, red proof run on both TFMs before and after the simplify rework)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (4 agents: reuse clean, simplification clean 0 findings, efficiency clean with 1 informational nit skipped [attach-first total-coverage construction beats a sub-microsecond per-failure allocation; the agent itself requested no change], altitude 1 finding APPLIED [the fault-observation backstop moved from the plant-throw arm to the endpoint's birth, so the normal path's settle-budget-timeout exit is covered by the same mechanism instead of a sibling arm remaining exposed] and 1 nit skipped [the pin's theoretical ~150ms false-pass window on a stalled test thread: the green side is deterministic by happens-before, the red side needs a >150ms stall of the test thread exactly at gate release, and widening costs linear test time; matches the file's established wall-clock red-proof idiom])
- [x] #10 /code-review high passed (0 correctness bugs; RC1 APPLIED: MarkEndpointFaultObserved's doc now states its scope honestly, the backstop covers only the fault-observation leg, the settle-inside-the-lifetime half lives on the throwing arm alone; RC2 FILED as JF-726 [the birth backstop is pinned by no test, every candidate pin shape recorded with its cost]; RC3 skipped with reason [the catch arm can delay the under-test failure by up to the 20s budget when the stranded endpoint hangs: deliberate, the budget is the one the normal settle uses and shortening it would re-strand, documented on EndpointSettleBudget])
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-10-03 in one worker round. The asymmetry is gone: in ServeInLockWarmCacheAsync
(VideoAudioControllerTests.cs, the one file in scope) a plantWarmCache (or park assert)
that throws now observes the parked endpoint BEFORE rethrowing. The try body was
restructured into an inner try/finally (gate release) wrapped by an outer catch that
calls ObserveStrandedEndpointAsync then rethrows with throw; (exception identity
preserved, Assert.Same-pinned): the observation is a bounded WaitAsync on the hoisted
EndpointSettleBudget (the ONE settle budget, previously inline at the normal settle)
whose outcome is deliberately swallowed, so the plant failure stays the exception the
caller sees while the settle is pulled inside the test's lifetime (fixtures alive,
static EncodeActive/EncodeGenerationCount writes land before teardown, and the await
itself observes an eventual fault). The simplify altitude round moved the
fault-observation backstop (MarkEndpointFaultObserved: OnlyOnFaulted +
ExecuteSynchronously continuation reading t.Exception) from the throwing arm to the
endpoint's BIRTH, closing the same unobserved-fault hazard on the normal path's
settle-budget-timeout exit (Task.WaitAsync's timeout does not observe the inner task);
its doc states the honest scope, the fault-observation leg only (RC1). Non-throwing
paths never enter the catch, so the 400ms park expectation and the probe-assert
ordering are untouched (class suite duration unchanged at ~5m50s per TFM). New pin
ServeInLockWarmCacheHelper_ThrowingPlant_ObservesStrandedEndpointBeforeRethrow: a
synthetic endpoint parks on a REAL per-item gate, holds a 150ms settle distance after
re-acquiring it, then faults; GREEN asserts the plant's own IOException surfaces by
identity AND the endpoint had settled before it surfaced (the flag is written inside
the endpoint before its fault, so its true value is reachable only through the
helper's await); RED PROOF run on both TFMs twice (before and after the simplify
rework): removing the catch's ObserveStrandedEndpointAsync call fails the pin with
the settle assert while the JF-681 sibling pin stays green. Gates: /simplify 4
agents, 1 applied, 2 skipped with reasons, 2 clean; /code-review high, 0 correctness
bugs, RC1 applied, RC2 filed as JF-726 (the birth backstop is unpinned, candidate pin
shapes recorded), RC3 skipped with reason. Suites on the FINAL state: 5008/5008
net9.0 AND net10.0 (baseline 5007 + 1), exit 0, only the documented pre-existing
xUnit1030 pair. Test-only change: no production surface, no deploy needed.
<!-- SECTION:FINAL_SUMMARY:END -->
