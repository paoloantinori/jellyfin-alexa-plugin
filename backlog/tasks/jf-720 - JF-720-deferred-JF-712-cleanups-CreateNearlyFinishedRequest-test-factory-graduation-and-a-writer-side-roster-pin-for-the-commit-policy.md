---
id: JF-720
title: >-
  JF-720 - deferred JF-712 cleanups: CreateNearlyFinishedRequest test-factory
  graduation and a writer-side roster pin for the commit policy
status: Done
assignee: []
created_date: '2026-10-02 23:59'
updated_date: '2026-10-03 18:28'
labels:
  - code-quality
  - testing
  - refusal-contract
dependencies:
  - JF-712
references:
  - >-
    backlog/tasks/jf-712 -
    JF-712-PlaybackNearlyFinished-advance-state-before-the-token-gated-build-a-refused-continuation-leaves-phantom-queue-pointer-and-zeroed-position.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-712 /simplify and /code-review rounds, carrying
the two findings both rounds judged real but out of that task's scope:

1. TEST-FACTORY GRADUATION (simplify reuse round, borderline note): the private
   `CreateNearlyFinishedRequest(token, offsetMs)` AudioPlayer-event factory now exists
   as FIVE per-file copies (GaplessPlaybackTests, PreEnqueueOnStartTests, RadioModeTests,
   ProgressiveQueueTests, and JF-712's PlaybackNearlyFinishedRefusalTests). The per-file
   copy is the established convention and JF-712 followed it, but the fifth copy is the
   natural graduation point: TestHelpers already owns the analogous intent-request
   factory (CreatePlayCommand) and would be the home for one shared
   `CreateAudioPlayerEventRequest(type, token, offsetMs)`; the five private copies then
   collapse to delegations (or deletions). Low value, low risk, one file plus five
   call-site files.
2. WRITER-SIDE ROSTER PIN (simplify altitude round, finding 3, judged optional there):
   the JF-712 derive-then-commit policy in PlaybackNearlyFinishedEventHandler is
   enforced by behavioral pins per arm (PlaybackNearlyFinishedRefusalTests), but a
   future FIFTH pre-build queue write in that handler is guarded only by the class-doc
   claim. The repo has the deeper idiom twice (SessionQueueReaderRosterTests,
   DeliveredLaunchOutputSpeechRosterTests, both IlCallScanner IL rosters): a
   writer-side roster asserting that every session-queue MUTATION site in
   PlaybackNearlyFinishedEventHandler is exactly
   {CommitPendingContinuation, TryFetchContinuationBatch's documented pre-build
   append, the entry rehydration mirror} would make the exception list structural the
   way JF-699's output-speech roster did for the launch gates. Pin requirement: the
   roster must be self-red (adding a raw NowPlayingQueue write anywhere in the handler
   flips it) and the three allowlisted sites must carry their in-code BY DESIGN
   markers (two of the three landed in JF-712 already).

AUDIT ADDENDUM (2026-10-03, JF-712 gate-marker round, finding 5): add the
SessionQueue.AppendUnseen(session, items) helper extraction to this task's scope. The
JF-712 commit leaves THREE hand-rolled copies of the copy-queue + seen-set +
add-unseen + assign idiom in PlaybackNearlyFinishedEventHandler (TryFetchContinuationBatch
~411-427, DeriveSimilarTracksPopulation ~718-727, CommitPendingContinuation ~925-940),
and the double-append race fix's soundness rests on the derive-time and commit-time
dedup computing the same predicate against the same store; as three inline loops, a
future edit to one compiles clean and silently breaks the invariant the race pin
encodes. ONE helper used by all three sites makes the drift structurally impossible
(and the roster pin this task already carries should then assert all three sites
route through it).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors # both projects build clean on net9.0 + net10.0, 0 warnings from the diff's files (the only warnings in the tree are the pre-existing xUnit1030 pair in the untouched VideoAudioControllerTests.cs)
- [x] #2 dotnet test passes # full suite 5027/5027 on BOTH TFMs on the final state (baseline 5019 + 8 new facts: 5 SessionQueueAppendUnseenTests, 3 PlaybackNearlyFinishedQueueWriteRosterTests), ~2m00s net9 / 2m03s net10 (the JF-730 dividend live)
- [x] #3 No new compiler warnings introduced # verified on the final state: the only warnings emitted are the pre-existing xUnit1030 pair in VideoAudioControllerTests.cs, a file this task never touched
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization # n/a: no session-attribute shapes touched; the only new state type is the QueueItem list the PendingContinuation record already carried
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress # n/a: no HTTP surface touched
- [x] #6 NLU test fixtures updated if interaction model changed # n/a: no interaction-model change
- [x] #7 E2E test added for new intent or handler logic # n/a: no new intent or production behavior; the extraction is behavior-preserving and its safety net is the existing PlaybackNearlyFinishedRefusalTests + RadioModeTests + ProgressiveQueueTests pins (all green unchanged) plus the new unit pins; no device-facing surface changed, so an E2E case has nothing new to assert
- [x] #8 Locale response strings added to all 17 locales # n/a: no response strings touched
- [x] #9 /simplify passed (no blocking cleanups remaining) # 4-angle agent round on the working-tree diff: applied 8 findings ( EventHandlerTests' pre-existing byte-equivalent CreateAudioPlayerRequest folded into the shared factory as a 6th delegation; SessionQueueAppendUnseenTests' local Track folded into TestHelpers.CreateSong; the dead committed intermediate; the derive-site comment trimmed to a pointer; the stale-prone "site N of 3" numbering dropped at all three sites; the owner ternary folded into the expectedSites tuple table; the routing pin rebuilt as a BOTH-DIRECTIONS SessionQueue member sweep (any future family member called from the handler is caught); the two-half family collapsed onto ONE TakeUnseen core so the derive/commit predicate is shared code by construction). Skipped 2 efficiency findings with the reviewer's own grading: the fetch site's per-candidate QueueItem projection allocations (batch-size-bounded, once per track-end) and the appended-list return consumed only for Count (the derive/commit symmetry the agreement pin exercises) - the int-returning variant would fragment the ONE-helper shape this task exists to create
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) # the high-effort fork ran its own verification battery on the mid-gate state (warnaserror build both TFMs, full suite 5027/5027, all three self-red probes re-planted and confirmed, the NowPlayingQueue setter confirmed side-effect-free against upstream) and returned 5 findings, ALL 5 APPLIED: the atomic-swap pin on the append shape (NotSame + old-instance-count, the ArtistIndexServiceTests idiom, reds an in-place-mutation rewrite of AppendUnseen that passes every other fact); the TestHelpers doc naming the deliberate RequestId drop instead of claiming byte-equivalence; the surviving twin factories delegated (PauseResumeStateTests.CreateStoppedRequest, EventHandlerTests' two inline PlaybackFailed constructions, GaplessPlaybackTests' inline PlaybackStarted negative-CanHandle) so the ONE-factory claim is now true of the tree; the SessionQueueReaderRosterTests exemption bullet gaining the JF-683 PlaybackFinishedEventHandler caller the pre-diff text omitted; and the AppendUnseen(IEnumerable<BaseItem>) overload with the ONE QueueItemFor projection so the BaseItem-to-QueueItem construction lives inside the family (the fetch site now reads SessionQueue.AppendUnseen(session, newItems))
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 22e74550, merged (with JF-715) as 9b82e9dc's tree. The SessionQueue.AppendUnseen/UnseenItems family: the derive-time and commit-time dedup the JF-712 race fix rests on are the same code by construction (one TakeUnseen core, one QueueItemFor projection); the writer-side roster pin holds the handler's entire outbound SessionQueue call surface to the documented roster in BOTH directions (three self-red probes against the real tree, each reverted byte-clean); the factory graduated with six delegating copies. The deliberate fetch-site unification verified inert against the real setter IL on both Jellyfin ABIs (the reviewer disassembled both). Worker gates green (simplify 8 applied; code-review high 5/5 applied incl. the atomic-swap pin); the orchestrator gate-marker verified all four scrutiny axes at source (the aliasing preserved as-given, the roster's six-entry surface statically enumerated, the dropped RequestId proven inert) with 2 below-line notes documented. Suites: worker and orchestrator independent 5027/5027 both TFMs, merged-tree 5046/5046 both TFMs exit 0 on both split legs at ~1m55s. Test-and-helper surface with one verified-inert production unification: deployed in the batched post-closure deploy of main's HEAD.
<!-- SECTION:FINAL_SUMMARY:END -->
