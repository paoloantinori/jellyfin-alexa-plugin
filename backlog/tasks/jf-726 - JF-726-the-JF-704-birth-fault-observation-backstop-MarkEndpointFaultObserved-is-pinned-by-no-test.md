---
id: JF-726
title: >-
  JF-726 - the JF-704 birth fault-observation backstop (MarkEndpointFaultObserved in
  VideoAudioControllerTests) is pinned by no test, so a silent regression to it fails
  nothing
status: To Do
assignee: []
created_date: '2026-10-03 07:29'
labels:
  - test-infrastructure
  - tech-debt
dependencies:
  - JF-704
references:
  - >-
    backlog/tasks/jf-704 -
    JF-704-a-throwing-plantWarmCache-strands-the-parked-endpoint-task-unobserved.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-704 /code-review round (effort high, finding
RC2). JF-704 hardened the ServeInLockWarmCacheAsync helper
(Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs) with two
mechanisms: the catch-arm settle await (ObserveStrandedEndpointAsync, which pulls the
stranded endpoint's settle inside the test's lifetime on the plant-throw /
park-assert arm) and a birth fault-observation backstop
(MarkEndpointFaultObserved: an OnlyOnFaulted + ExecuteSynchronously continuation
reading t.Exception, attached immediately after startEndpoint()). The first is
pinned end to end by
ServeInLockWarmCacheHelper_ThrowingPlant_ObservesStrandedEndpointBeforeRethrow
(red proof: removing the catch-arm call fails the pin on both TFMs). The second is
pinned by NOTHING: every existing pin stays green with the backstop silently
gutted, because on the pinned arm the observation is provided by the catch-arm
await, not the backstop.

The exit the backstop UNIQUELY covers is the settle-budget timeout on the normal
path: Task.WaitAsync's timeout does NOT observe the inner task, so an endpoint that
neither completes nor faults within EndpointSettleBudget (20s) leaves the helper
via the TimeoutException with the endpoint still running, and only the backstop
observes its eventual fault. REGRESSION SHAPE (any of): dropping the OnlyOnFaulted
flag, emptying the continuation body, or deleting the MarkEndpointFaultObserved
call at the birth site. Every one of those keeps the full 5008-test suite green
while re-creating the unobserved-fault stranding on the timeout exit.

Why it was not pinned in JF-704: exercising the timeout exit costs the whole 20s
budget per TFM in every suite run, and the settle-budget field is a static readonly
TimeSpan with no seam to shrink it per test. Candidate pin shapes, in rising cost:

1. IL/roster SHAPE PIN in the family of the JF-722 CaptureRefreshPairingTests IL
   roster: scan the test assembly's ServeInLockWarmCacheAsync method body and pin
   that it attaches a TaskContinuationOptions.OnlyOnFaulted continuation before
   the park assert. Cheap and deterministic; pins the mechanism's presence, not
   its observation effect.
2. A BUDGET SEAM: make EndpointSettleBudget overridable per test (an optional
   parameter or an internal setter), then a timeout-exit pin can construct the
   hanging-endpoint shape at a ~1s budget and assert the plant/settle failure
   surfaces while the eventual fault stays observed (assertable via a
   TaskCompletionSource the endpoint completes with a fault after the helper
   returns, then reading task.Exception inside the test before it ends; the
   backstop's own observation is still only provable via the UnobservedTaskException
   event, which is finalizer-timing flaky, so the pin would prove the CALL SHAPE
   plus the seam behavior rather than the runtime observation).
3. The direct runtime proof (TaskScheduler.UnobservedTaskException + forced GC
   collection) is rejected as flaky by design; do not build it.

Also recorded here, from the same review round's RC1 resolution: the backstop
covers ONLY the fault-observation leg of the JF-704 harm. On the timeout exit the
endpoint still runs its encode past the test's lifetime and still lands static
registry writes after teardown; pulling the settle inside the test's lifetime
happens solely on the throwing arm. A fix for THAT leg on the timeout exit is the
20s wait itself (i.e. accepting the budget), so the honest options are the seam of
shape 2 or explicitly declaring the timeout exit best-effort.
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
