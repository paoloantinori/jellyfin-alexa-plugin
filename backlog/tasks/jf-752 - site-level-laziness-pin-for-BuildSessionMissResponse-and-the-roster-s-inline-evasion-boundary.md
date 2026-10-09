---
id: JF-752
title: >-
  JF-752 - site-level laziness pin for BuildSessionMissResponse (the side
  effects, not just the response shape) + the EventAwareDegradeRoster's
  inline-evasion boundary pairing
status: Done
assignee: []
created_date: '2026-10-04'
labels:
  - test-coverage
  - pipeline
dependencies:
  - JF-708
references:
  - backlog/tasks/jf-708 - unify-the-refusal-translation-family-one-policy-for-warmingtoken-refusals-and-the-event-aware-degrade.md
priority: low
---

## Description

## Definition of Done

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-04 by the orchestrator from the JF-708 gate-marker (finding 2 of 4,
low severity; the reserve number the JF-708 worker left unused).

FINDING: the session-miss laziness guarantee (BuildSessionMissTell's dead-token
branch reads DeviceQueueManager.GetLastPlayedItemId and writes the AccountRelink
Information log, which must never fire on event requests; the pre-JF-708 early
return guaranteed it, JF-708's Func laziness keeps it) is pinned only at the
CORE's generic level (EventAwareDegradeCoreTests) and structurally by the IL
conjunction roster. A future edit that hoists the dead-token evidence OUT of the
factory (e.g. computing hadPreviousPlay in BuildSessionMissResponse before the
DegradeForEventRequest call) references neither IsEventRequest nor
BuildKeepAliveResponse, so BOTH guards stay green while every session-miss event
request re-fires the log and the queue read.

THE WORK: a site-level pin driving BuildSessionMissResponse (via its public
caller or reflection) with an event request, a token-bearing user, and an
observable DeviceQueueManager seam, asserting (a) the keep-alive response shape
AND (b) GetLastPlayedItemId was never called and the AccountRelink log never
fired; plus the non-event twin asserting both DO fire. The harness question
(Plugin.Instance is a static singleton; the DeviceQueueManager seam needs either
the shared-instance mock the plugin collection provides or a seam extraction) is
the pin's design decision.

PAIRS WITH: the EventAwareDegradeRoster's SECOND BOUNDARY (documented on the
roster since the JF-708 tail): an inline pattern evasion (a fifth degrade copy
spelling the type test itself while calling BuildKeepAliveResponse directly)
escapes the IL conjunction scan; a site-level side-effect pin per folded site is
the only guard class that catches per-site divergence regardless of how it is
spelled. Scope: the four JF-708 folded sites as they exist then; a blanket
side-effect framework is NOT wanted.

CORRECTIONS (2026-10-04, the simplify altitude round on the JF-708 tail, folded
before anyone picks this up): (1) HALF THE WORK ALREADY EXISTS at the site
level: EventHandlerTests.cs:1601
(HandleRequestAsync_EventRequest_SessionNotFound_DeadToken_KeepsKeepAlive)
already drives the session-miss site with an event request in the dead-token
shape and asserts the keep-alive SHAPE; the residual gap is assertion (b) only
(the side effects), since that shape test stays green under the hoist. (2) The
harness question is ANSWERED by the existing suite: there is no shared-instance
mock; the working seam is a real DeviceQueueManager attached via the internal
setter + TestHelpers.CreateDeviceQueueManager/SwapPluginQueueManager (the
JF-630 ONE swap scope) + CreateSessionMissHarness at EventHandlerTests.cs:1431,
one file away. (3) Assertion (b)'s first half is NOT implementable as stated:
DeviceQueueManager is public sealed with a non-virtual GetLastPlayedItemId, so
Moq cannot spy it and the read leaves no side effect; only the LOG half is
directly assertable with a capturing logger, and the read half needs a seam
extraction (a production change) or a relaxed design (assert the log only, and
guard the read by inspection) - the design decision is narrower than the
original filing implied. (4) Wording: hoisting hadPreviousPlay alone re-fires
the queue read, not the log (the log sits inside the lazy factory, gated on
HasJellyfinToken && hadPreviousPlay); both fire only under a deeper
de-lazification. SITE PATHS: BaseHandler.cs BuildSessionMissResponse :707,
BuildSessionMissTell :722, the public caller at :321.

## DESIGN DECISION (the worker, 2026-10-04): LOG-ONLY PIN, no production seam

Chosen: the log-only pin (test-only change). The AccountRelink Information log
is asserted absent on the event leg and present on the intent leg; the
GetLastPlayedItemId read is NOT asserted and stays guarded by structure (the
read lives inside BuildSessionMissTell, referenced only by the lazy factory)
plus the strengthened contract comment on BuildSessionMissTell ("keep the read
HERE, inside the factory").

Why not the seam extraction (interface on the read, or unseal + virtual):

1. The read half is not merely non-spyable (public sealed, non-virtual
   GetLastPlayedItemId, pure in-memory dictionary lookup, no side effect, no
   log of its own): it is semantically uncountable at this site. The SAME
   session-miss request already performs one legitimate read before the
   degrade runs, in the JF-588 self-heal predicate
   (BaseHandler.SelfHealSessionAsync, runs for event and intent requests
   alike), so the laziness guarantee is "no SECOND read", and even a
   hypothetical spy could only assert a call count against a read that
   correctly fires once. The original filing's "GetLastPlayedItemId was never
   called" is false as literally stated; correction 4's narrower wording is
   the truth.
2. The production cost is disproportionate to a low-severity coverage task:
   unsealing DeviceQueueManager (a concurrency-careful class) or retyping the
   public Plugin.DeviceQueueManager property to an interface touches every
   consumer to buy a counter whose only target is the shallow-hoist evasion.
3. The log is the side effect with operational meaning: it drives dead-token
   triage, and a de-lazification that re-fires it spams and misdirects on
   every session-miss event request. The read re-firing alone costs one
   in-memory dictionary lookup.

EMPIRICAL VERIFICATION (both probes on the unmodified tests, both TFMs):

- RED-PROOF (deep de-lazification: BuildSessionMissResponse eagerly builds the
  tell, the realistic refactor shape): the event pin FAILS with exactly the
  AccountRelink Information line in the capture, both TFMs; the intent twin
  stays green. The pin catches the observable half.
- BOUNDARY PROBE (shallow hoist, the filing's correction 4 shape: only
  hadPreviousPlay leaves the factory, parameter-threaded; the log stays
  inside): BOTH tests stay green, both TFMs. The accepted boundary of the
  log-only design is real, not asserted: the shallow hoist escapes this pin
  and remains guarded by structure + inspection, exactly as documented.

SCOPE REDUCTION (the "PAIRS WITH ... four folded sites" paragraph): verified
by inspection that the session-miss site is the ONLY one of the four JF-708
folded surfaces whose non-event leg has observable side effects. The other
three build a pure single Tell with no side effects on either leg:
BuildUserNotFoundResponse and the RequestPipeline refusal translation use the
lazy Func overload with pure ResponseStrings lambdas, and the controller's
four catch-path degrades use the eager string overload with constant/pure
messages (the core's doc comment blesses eager composition for exactly that
pure shape). A side-effect pin at those three sites would assert the absence
of nothing; the session-miss pair IS the side-effect coverage for the folded
family. The roster's second-boundary note now points here accordingly.

## IMPLEMENTATION

- EventHandlerTests.cs, new "JF-752" family after the JF-527/JF-507 shape
  tests, reusing the existing seams verbatim (CreateSessionMissHarness,
  RecordPreviousPlayOnHarnessDevice's internal-setter attach, and the shared
  TestCaptureLogger provider on the handler's logger factory):
  - HandleRequestAsync_EventRequest_SessionNotFound_DeadToken_NeverFiresAccountRelinkLog
    (event request + token-bearing user + recorded previous play: asserts the
    keep-alive shape restated AND no AccountRelink record captured);
  - HandleRequestAsync_IntentRequest_SessionNotFound_DeadToken_FiresAccountRelinkLog
    (the non-event twin: the leg speaks the relink tell, pinned by the JF-527
    tests above, and this test asserts the AccountRelink Information record
    IS captured; the read's firing on this leg is transitively pinned by that
    relink tell, which cannot be spoken without the factory's read returning
    the evidence).
- EventAwareDegradeRosterTests.cs: the SECOND BOUNDARY note updated from
  "filed as JF-752" to the landed pin's location.
- BaseHandler.cs BuildSessionMissTell doc comment: extended with the pin
  pointer and the read-half contract (comment-only; no production behavior
  change in this task).

- [x] #1 dotnet build passes with 0 errors (Release -warnaserror --no-restore on the solution, final state: Build succeeded, 0 Warnings, 0 Errors)
- [x] #2 dotnet test passes (full suite, both TFMs, ONE run on the final state: 5192/5192 net9.0 AND net10.0; the diff adds exactly 2 [Fact]s and touches no other test-bearing file, so the pre-change tree derives to 5190 - the 5171 recorded in the JF-645 merge message for this same tree does not reconcile, the same branch-tree-vs-merged-tree arithmetic caveat that message itself documents for prior merges; the measured run is the truth)
- [x] #3 No new compiler warnings (the Release -warnaserror run is the proof)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A: no session-attribute shape touched)
- [x] #5 HttpClient instances not shared across calls modifying BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E added for new intent/handler logic (N/A: no new intent; the pin is unit-level by design)
- [x] #8 Locale strings in all 17 locales (N/A: no new locale string; the pin asserts an existing log)
- [x] #9 /simplify passed (4 agents: reuse 0 findings; efficiency 0 findings; altitude 0 findings, the site-level pair adjudged the only observable altitude and the two-test shape a calibrated absence pin; simplification 4 findings ALL applied - the event pin's partial 2-of-4 shape restatement completed to the full four (the dispatch mandates the shape half; the agent's own honest-form alternative), the intent twin trimmed to its new content (the log assertion; the tell stays with the JF-527 sibling), the event test's 19-line rationale cut to 9 lines with a pointer to the canonical contract on BuildSessionMissTell, the twin's transitive-pinning rationale dropped)
- [x] #10 /code-review high passed (0 correctness bugs; 5 findings: 3 applied - the roster note's "compose eagerly" mechanism corrected to "build a pure single Tell" with the RequestPipeline lazy-Func fact verified at RequestPipeline.cs:109-111 and the same correction carried into this file's SCOPE REDUCTION, the IMPLEMENTATION bullet's overstated twin assertion surface fixed, the Final Summary evidence gap filled by this rewrite; 1 applied pre-commit - the untracked worker scratch script deleted; 1 declined with reason - the event pin's shape restatement is the dispatch-mandated self-containment, documented in-code, lockstep-edit cost accepted)
- [x] #11 RED-PROOF executed (deep de-lazification sabotage flipped the event pin on both TFMs with the AccountRelink line in the failure capture; sabotage reverted, tree verified clean)
- [x] #12 Design decision documented (the section above; log-only, with the empirical boundary probe)

Two site-level side-effect pins landed in EventHandlerTests' JF-752 family
(event leg: keep-alive shape restated in full AND no AccountRelink record
captured; intent leg: the AccountRelink Information record IS captured), the
roster's second-boundary note repointed at the landed pin, and
BuildSessionMissTell's contract comment extended to carry the read-half guard
(comment-only; the only production-code change in this task). Design
decision: log-only, no production seam; the read half is uncountable at this
site (sealed non-virtual read; the JF-588 self-heal predicate already reads
once per miss) and stays guarded by structure plus the contract comment.
Both probes verified empirically: the deep de-lazification flips the event
pin (red, both TFMs), the shallow read-only hoist does not (the documented
boundary). The other three folded sites build a pure single Tell on either
leg (verified by inspection), so the session-miss pair is the folded family's
side-effect coverage.

Evidence: full suite 5192/5192 both TFMs on the final state (one run);
solution Release --no-restore -warnaserror Build succeeded 0 Warnings 0
Errors; affected families 112/112 both TFMs on the restored post-probe state.
Reserved filing number JF-762 (corrected from JF-760 mid-task) went UNUSED:
no out-of-scope finding survived the gates unapplied.
<!-- SECTION:NOTES:END -->
