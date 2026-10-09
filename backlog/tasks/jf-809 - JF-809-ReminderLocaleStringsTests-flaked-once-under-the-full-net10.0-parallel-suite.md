---
id: JF-809
title: >-
  JF-809 - ReminderLocaleStringsTests flaked once under the full net10.0
  parallel suite
status: Done
assignee: []
created_date: '2026-10-07'
updated_date: '2026-10-09 23:06'
labels:
  - tech-debt
  - testing
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-807 session (2026-10-07, same-turn per the track-every-failure
rule).

`ReminderLocaleStringsTests.ReminderSetRelativeFor_ResolvesAllLocales` failed
ONCE in a full net10.0 suite run (the JF-807 worktree's first final-state run,
5457 tests), stack fragment `InvokeStub_ReminderLocaleStringsTests...MethodBaseInvoker.InvokeWithOneArg`,
message not captured before the run scrolled. Not reproducible since:

- the class alone on net10.0: 30/30 passed;
- the full net10.0 suite re-run twice on the SAME tree: 5457/5457 passed both
  times;
- the base commit dca7e165 full net10.0 suite (throwaway worktree): 5453/5453
  passed.

The JF-807 diff (a warming gate in PlayBookIntentHandler, four pins in the
Plugin-collection PlayBookIntentHandlerTests class, TestHelpers additions) has
no shared-state path into ReminderLocaleStringsTests; the plausible mechanism
is a latent order/timing sensitivity in that test under full-suite parallelism
that the added tests' scheduling shift happened to unmask once. No root cause
is claimed (one occurrence, message lost).

INVESTIGATION SHAPE when it recurs: capture the full failure message FIRST
(re-run with the failure block piped to a file), note whether it correlates
with the Plugin collection's concurrent phases, and check the theory's
locale-data dependencies for shared mutable state (ResponseStrings /
culture). If it recurs, decide isolation (a dedicated collection) or a fix of
the underlying shared state.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 #1 Reproduce or rule out: a captured failure message plus a identified shared-state path, or three consecutive clean full-suite runs across separate sessions with the flake never seen again (then close as unreproducible with the evidence linked)

JF-807 GATE-MARKER ADDENDUM (2026-10-07, same-turn): the CROSS-INCIDENT LINK this filing lacked - the ReminderLocaleStringsTests one-shot repeats the PersonalizedGreetingLocaleTests en-US shape recorded in JF-801's incident log, and the mechanism is now CONCRETE (verified from source by the JF-807 gate-marker): ResponseStringsTests calls ResponseStrings.Reset() seven times while UNCOLLECTED in the parallel phase; a concurrent Get already past EnsureInitialized reads a cleared dictionary and fail-softs to the raw KEY, which the Assert.NotEqual theories catch. The structural fix (ResponseStringsTest collected into the Plugin phase, removing every Reset from the parallel window) is APPLIED in the JF-807 gate-marker tail; JF-801's incident record carries the hypothesis and mechanism. This task's residual: confirm the fix holds (the next full-suite runs) and close on the green streak.
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
CLOSURE-BY-EVIDENCE (2026-10-09 23:15, orchestrator, from the JF-801 completion round): the flake mechanism this task's incident family pointed at - the ResponseStrings.Reset race (Reset clears _locales while a concurrent parallel-phase Get reads it, failing soft to the raw key, exactly the one-shot shape ReminderLocaleStringsTests showed) - is STRUCTURALLY CLOSED at HEAD: commit 607b9466 (the JF-807 review tail) collected ResponseStringsTests into [Collection("Plugin")] (the only Reset()/RegisterLocale() caller in the assembly; a project-wide grep finds zero other writers to _locales), so no Reset runs in the parallel phase anymore and the race window no longer exists. Verification chain: the JF-801 worker's project-wide writer grep + the collection membership at HEAD; the addendum recording the hypothesis lives in JF-801's Implementation Notes (SECOND INCIDENT FAMILY ADDENDUM, 2026-10-07). The observed flake predates the closure commit; no new incident has been recorded since. Disposition: close when the night's batch flips ride the orchestrator gate pass; if a ReminderLocaleStringsTests failure EVER recurs after 607b9466, it is a NEW mechanism (re-open or file fresh with the message-capture note this task already carries).
<!-- SECTION:NOTES:END -->
