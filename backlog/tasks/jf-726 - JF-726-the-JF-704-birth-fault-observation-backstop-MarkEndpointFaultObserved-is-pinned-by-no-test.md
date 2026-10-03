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
- [x] #1 dotnet build passes with 0 errors (Debug and Release, both TFMs; the only warning is the PRE-EXISTING xUnit1030 site at the JF-681 pin's ConfigureAwait, identical on HEAD and failing main's CI since JF-681: filed same-turn as JF-736, not introduced here)
- [x] #2 dotnet test passes (full suite ONCE on the final state, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`: 5047/5047 net9.0 and 5047/5047 net10.0, baseline 5046 + the one new pin)
- [x] #3 No new compiler warnings introduced (verified by build-output inventory: the diff adds zero warning sites; the xUnit1030 warning is byte-identical code on HEAD, line-shifted only)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: test-infrastructure change, no session state)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-infrastructure change; the new pin ServeInLockWarmCacheHelper_BirthBackstop_AttachesOnlyOnFaultedContinuationBeforeParkAssert IS the added coverage, with red proofs run on both TFMs for all five degradation shapes: dropped flag, emptied body, deleted call, late attachment, wrong-task argument)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (4 agents: 2 APPLIED - the ordering anchor generalized from Assert.False to any Xunit.Assert callee so an assert-flavor rewrite cannot false-red, and the offenders/birthCalls pair collapsed into one collection with one count+site assert; SKIPPED with reasons - the ldc short-form risk is impossible by arithmetic (any constant carrying OnlyOnFaulted = 0x50000 exceeds every short encoding, proof now on LdcI4Operands), the Assert.True null-guard kept over Assert.NotNull for the house message form, the offset-storage nit moot after the collapse; efficiency clean, the assembly walk measured ~30ms against the precedent's ~9ms; 1 out-of-scope finding FILED as JF-736 finding 2: the local InstructionOperands walk duplicates IlCallScanner.OperandTokens and belongs in the shared scanner)
- [x] #10 /code-review high passed (6 findings: RC1 APPLIED - the continuation-body acceptance set scoped to this class and pinned to exactly one body, so a same-named helper in another test class cannot satisfy the Exception-read assert; RC2 APPLIED - the argument-identity tie added, the birth call's ldfld field must be the one stored immediately after startEndpoint's Invoke, red-proven with a wrong-task mutation; RC4 APPLIED - the options assert tightened from a bits-mask to the exact documented OnlyOnFaulted|ExecuteSynchronously constant with the phantom-window residual documented and cross-referenced to JF-736; RC6 APPLIED - MarkEndpointFaultObserved's stale "No dedicated pin" doc sentence replaced with the JF-726 pin reference and the honestly-scoped residual; RC3 FILED as JF-736 finding 2 (same site the simplify round found, out of the single-file surface); RC5 SKIPPED with reason - the named-method refactor of the lambda reds the pin loudly by design, the documented conscious-update path, and pinning the ContinueWith target via ldftn would grow the IL parsing past the roster idiom for no added regression coverage)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CHOSEN SHAPE: the filing's candidate 1, the IL-roster presence-and-shape pin
(the CaptureRefreshPairingTests idiom), landed INSIDE VideoAudioControllerTests
as ServeInLockWarmCacheHelper_BirthBackstop_AttachesOnlyOnFaultedContinuationBeforeParkAssert
plus three small local scanner helpers (CallInstructionOffsets / LdcI4Operands
over one InstructionOperands byte-walk). What it pins, all verified against the
real emitted IL (probed before writing, not assumed): the backstop methoddef
exists exactly once; it is called from exactly one place, the
ServeInLockWarmCacheAsync family (the endpoint's birth); the call precedes the
helper's first assert call (the park assert, an ordering fact anchored on any
Xunit.Assert member); the call's ARGUMENT is the field the startEndpoint
delegate's result was just stored into (the code-review RC2 argument-identity
tie: ldfld-adjacency plus the stfld-immediately-after-Invoke store, raw token
equality); the options operand is the exact documented
OnlyOnFaulted|ExecuteSynchronously constant; and this class's one
compiler-generated continuation body reads Task.Exception. RED PROOFS, all run
on BOTH TFMs against the final assert shapes: dropping the OnlyOnFaulted flag,
emptying the continuation body, deleting the birth-site call, attaching after
the settle instead of at birth, and passing a task other than the started
endpoint each red a distinct assert (the late-attachment proof also shows the
ordering assert catches what the one-caller assert alone would pass).

WHY THIS SHAPE AND NOT THE ALTERNATIVES: a behavioral pin of the backstop's
unique exit (the settle-budget timeout) costs the whole 20s EndpointSettleBudget
per TFM in every suite run, and the runtime observation effect is provable only
via TaskScheduler.UnobservedTaskException after a forced GC (finalizer-timing
flaky, candidate 3, rejected in the filing itself) or via a marker inside the
continuation, which was rejected here because it MODIFIES the mechanism under
test and breaks its zero-allocation static-lambda shape (the property the
JF-704 doc names); the filing's candidate 2 budget seam was not taken because
it grows the helper's signature and the settle-budget field's contract to
prove the seam's plumbing, still not the observation (the filing itself records
that limitation). What remains honestly unpinned is exactly the runtime
observation EFFECT; the codebase-specific regression surface (flag, body, call
site, birth ordering, argument identity) is now fully covered at a run cost of
~30ms per TFM. The empirical probe also corrected two assumptions before they
could ship wrong: ExecuteSynchronously is 0x80000 (the combined constant is
0xD0000), and the continuation lambda emits as <>c.<MarkEndpointFaultObserved>b__N_M.

GATES: /simplify (4 agents, outcomes in DoD #9) and /code-review high (6
findings, outcomes in DoD #10) both run as literal Skill calls in the worker
transcript; two findings were applied from each round and the one real
out-of-scope item (the IlCallScanner byte-walk duplication, flagged by every
angle) is JF-736 finding 2. DISCOVERED AND FILED AS JF-736 (finding 1, HIGH):
main's CI has been red since JF-681 (25+ runs, last green 7bc89eb1) because
the lock-release pin's ConfigureAwait(false) fails the Release -warnaserror
build with xUnit1030; not caused by this task, evidence and fix options in the
JF-736 file. SUITES: full suite once on the final state, 5047/5047 both TFMs
(baseline 5046 + 1); the pin additionally verified green in Release on both
TFMs. Test-only surface: no production code, no interaction models, no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
