---
id: JF-720
title: >-
  JF-720 - deferred JF-712 cleanups: CreateNearlyFinishedRequest test-factory
  graduation and a writer-side roster pin for the commit policy
status: To Do
assignee: []
created_date: '2026-10-02 23:59'
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
All three items landed, and the audit addendum's concern turned out to be the load-bearing one.

1. AppendUnseen family (the addendum item): SessionQueue gains AppendUnseen (commit half: copy + seen-dedup + append + conditional whole-list replace, returning the appended items AS THE GIVEN INSTANCES) and UnseenItems (derive half: pure unseen collection for the JF-712 derive-then-commit arms), both delegating to ONE private TakeUnseen core, so the derive-time and commit-time dedup the double-append race fix rests on is the same loop by construction, and the /simplify round's shared-projection addition put the BaseItem-to-QueueItem construction in one QueueItemFor beside it. The three hand-rolled sites in PlaybackNearlyFinishedEventHandler route through the family: TryFetchContinuationBatch (pre-build, BY DESIGN marker kept), DeriveSimilarTracksPopulation (derive-only), CommitPendingContinuation (the ONE commit point; the JF-712 known-race note now lives on the helper doc, the lock's home if it ever bites). One deliberate unification, documented: the fetch site's old UNCONDITIONAL equal-content replace on an all-dupes batch is now the no-write-on-no-change shape the commit always had (the code-review round verified the setter is side-effect-free upstream, and no test pinned the instance swap).

2. Writer-side roster pin: PlaybackNearlyFinishedQueueWriteRosterTests (IlCallScanner idiom, scoped to ONE handler like CaptureRefreshPairingTests) has three facts: the handler performs ZERO direct set_NowPlayingQueue calls (empty allowlist by policy); its ENTIRE outbound SessionQueue call surface - the three append sites PLUS the reader legs (IndexOfQueueItem x2, the episode arm's IdSet skip-set) - equals the documented roster in both directions, so a future SessionQueue member called from anywhere in the handler is caught even though no named-target scan could know it (the altitude round's completion of the named-target pin); and the rehydration mirror is called only from HandleAsync. SELF-RED PROVEN with three probes against the real tree, each then reverted byte-clean: probe A (raw write in ResolveNextItemId) reds the setter fact alone; probe B (the idiom re-inlined at the commit) reds BOTH setter and surface facts; probe C (a new family member call in HandleAsync) reds the surface fact alone with the offender named. Honest boundaries documented: in-place mutation through the getter is invisible to the setter probe; mutations inside other types except the pinned rehydration leg; the surface fact pins routing, the predicate is TakeUnseen's shared code plus the behavioral agreement pin; handler-local scope; resolution-skip behavior per fact.

3. Test-factory graduation (the original item): TestHelpers.CreateAudioPlayerEventRequest(type, token, offsetMs); the five per-suite CreateNearlyFinishedRequest copies collapsed to one-line delegations keeping their scene defaults (the refusal suite's 120_000 offset, the nullable token), with the ONE deliberate delta named in the doc (PreEnqueueOnStartTests' inert RequestId = "test-req" dropped). The /simplify reuse round found the SIXTH copy EventHandlerTests had carried type-general all along - it delegates now too, and the remaining same-shape twins (PauseResumeStateTests.CreateStoppedRequest, EventHandlerTests' PlaybackFailed inlines, GaplessPlaybackTests' PlaybackStarted negative) were folded in the review round so the ONE-factory claim is true of the tree; the JSON-deserializing AlexaRequestFactory variant stays separate by design (it populates the readonly requestId/timestamp/locale fields).

AlbumPlayService check (the dispatch's JF-713 note): its additions are NOT the append-unseen shape - a fresh-list whole-queue REPLACE (ordered, so MirrorQueueToSession can read metadata) and the device-queue DeriveShuffledQueue/CommitShuffledQueue pair - no copy-queue + seen-set + dedup anywhere in it (repo-wide IdSet/seen.Add sweep confirms the only append-unseen sites were the handler's three). It does not join the callers; noted for the record.

Runs: filtered suites after every round (118 after extraction, 109/253/79 through the gate rounds, 15 after the probes); full suite ONCE per TFM on the final state 5027/5027 net9.0 and net10.0 (baseline 5019 + 8 new facts). Gate runs: /simplify 4 parallel angles then /code-review high (its fork re-ran the battery independently). No production behavior change; no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

