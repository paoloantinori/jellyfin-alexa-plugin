---
id: JF-809
title: >-
  JF-809 - ReminderLocaleStringsTests flaked once under the full net10.0 parallel suite
status: To Do
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - testing
references: []
priority: low
---

## Description

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

## Definition of Done
- [ ] #1 Reproduce or rule out: a captured failure message plus a identified shared-state path, or three consecutive clean full-suite runs across separate sessions with the flake never seen again (then close as unreproducible with the evidence linked)
