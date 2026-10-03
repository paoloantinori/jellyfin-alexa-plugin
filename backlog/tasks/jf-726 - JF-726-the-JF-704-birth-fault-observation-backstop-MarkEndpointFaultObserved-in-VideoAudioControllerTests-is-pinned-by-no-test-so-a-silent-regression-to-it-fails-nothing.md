---
id: JF-726
title: >-
  JF-726 - the JF-704 birth fault-observation backstop
  (MarkEndpointFaultObserved in VideoAudioControllerTests) is pinned by no test,
  so a silent regression to it fails nothing
status: Done
assignee: []
created_date: '2026-10-03 07:29'
updated_date: '2026-10-03 20:18'
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

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit e940f9b3 + the rebase merge,
6 findings; the reviewer closed the constants axis against the COMPILED merged-tree DLLs
by byte-scanning both TFMs and executing the pin green, and re-derived the byte-walk
bounds): F3 already satisfied by the orchestrator's rebased full-suite run (5068/5068
both TFMs exit 0 on the exact merged tree). F4 APPLIED (the birth-caller fact gains the
TopLevelType confinement the continuation-body set already carried). F2 APPLIED (the
body-scoped receiver tie: MarkEndpointFaultObserved's body must begin with the ldarg.0
parameter load, making the one ContinueWith's receiver the parameter by construction in
the straight-line body - the RC2 call-site tie stopped at the method boundary). F5
APPLIED (the LdcI4Operands impossibility doc scoped to non-negative enum-folded
constants). F1 APPLIED (the JF-736 task file annotated: its finding 1 fixed by 9353de90,
live scope narrowed to the scanner hoist). F6 already tracked as JF-736 finding 2.
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
Closed by the orchestrator after the full cycle: worker commit e940f9b3 + the rebase onto the CI fix + gate-marker tail 27498ba5, merged as 103d8e9e. The birth fault-observation backstop pinned: the IL-roster shape pin holding six facts (one methoddef; one birth-site caller, TopLevelType-confined after the tail; the call-before-first-Assert ordering anchored on any Xunit.Assert member; the RC2 argument-identity tie; the exact 0xD0000 continuation-options constant; the one continuation body reading Task.Exception), plus the gate-marker tail's body-scoped receiver tie (the ldarg.0 first-real-instruction fact, its Debug-nop prefix discovered by the fact's own red proof). Five degradation red proofs on both TFMs; the IL reality probed before writing (0x80000+0x50000=0xD0000; the <>c emission shape); the orchestrator reviewer re-verified the constants against the compiled merged-tree DLLs by byte-scanning both TFMs and executing the pin. The worker's warning verification DISCOVERED main's CI-red streak (25+ runs since JF-681), verified against live run logs and fixed on main as 9353de90 (green run 37149291498). JF-736 filed (the IlCallScanner scanner hoist, the live scope after the CI half's fix). Worker gates green (simplify 2 applied, 3 skipped with reasons incl. the arithmetically-impossible ldc short-form; code-review high 4 applied, 1 filed, 1 skipped); the orchestrator review's 6 findings all landed (F3 already satisfied by the rebased run). Suites: worker 5047/5047 both TFMs plus Release pin runs; orchestrator rebased 5068/5068 both TFMs exit 0 on the exact merged tree; merged-tree 5068/5068 both TFMs exit 0 on both split legs. Test-only: no production surface, no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
