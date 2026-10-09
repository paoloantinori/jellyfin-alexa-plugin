---
id: JF-712
title: >-
  JF-712 - PlaybackNearlyFinished advances recovery/queue state before the
  token-gated build, so a refused continuation leaves a phantom pointer and a
  zeroed position
status: Done
assignee: []
created_date: '2026-10-02 15:05'
updated_date: '2026-10-02 22:37'
labels:
  - playback
  - progressive-queue
  - refusal-contract
dependencies:
  - JF-699
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-699 merge
(commit 65821618, finding 1 of 5; the review verified all six scrutiny axes clean
otherwise). The JF-699 reorder policy (launch-before-state-write) was applied at 13
handler sites but NOT inside PlaybackNearlyFinishedEventHandler - the exact handler the
new pipeline comment names as the reachable event-request refusal. Two arms violate
it: the main continuation arm calls UpdateRecoveryPointer (queue MoveTo + SetCurrentItemPointer
+ CurrentPositionTicks=0) at ~line 262 BEFORE BuildAudioPlayerResponse at ~287, and the
cached-enqueue arm repeats the shape (~155 before ~159); AutoPopulateRadioTracks and
AutoPopulatePostPlayTracks queue writes (~203/~221) precede their builds the same way.

Failure scenario: empty StreamTokenSecret while a podcast plays at 1.5x (or an EAC3
episode on the audio route): each PlaybackNearlyFinished advances the device queue
pointer to the next item, then the token-gated atempo/transcode source throws
StreamTokenNotConfigured; nothing enqueues, Amazon gets the keep-alive, and the
finishing item's recorded position is zeroed. After the config is fixed, the recovery
read (TryRehydrateSessionQueueFromDevice) names a track that never played and resumes
from the wrong item at position 0.

THE WORK needs a design decision, which is why it is filed rather than tailed: the
build CONSUMES the pointer it would need to follow (the enqueue builds the item the
pointer names), so a plain reorder is not mechanical. Two shapes: (a) derive-then-commit
- compute the next-item resolution without committing the pointer/position, build the
response, then commit the state only after a successful build (mind the JF-691 enqueue
record and the JF-579 rehydration reader contract on the queue view); or (b)
compensate-on-refusal - catch StreamTokenNotConfigured at the two arms, roll the
pointer back to the finishing item and restore its position ticks, rethrow (the catch
must not swallow; the pipeline translation still owns the response). Pin: a refused
continuation leaves the pointer naming the finishing item with its position intact,
and a successful continuation still advances exactly once (the existing continuation
pins must stay green unchanged).

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit 343c478f, 5 findings; all
five scrutiny axes verified clean at source level: the three compensation rejections,
the build's provable non-read of the pointer, the JF-447/JF-691 orderings, the safe
pre-build exceptions, and the RadioModeTests repair's genuineness): F2 APPLIED (the
pre-build Remove comment stated a FALSE invariant - two reachable shapes drop a live
continuation, both pre-existing; the comment now names them so no future reader trusts
a stronger invariant than the code provides); F1 APPLIED (the KNOWN RACE note at the
commit's whole-list replace: the re-dedup prevents double-append, NOT lost update; the
lock belongs with the JF-720 helper extraction if the shape ever bites); F3 APPLIED
(the BOUNDED COST note on the derive-on-refire shape under sustained refusal); F4
APPLIED (the repaired RadioMode pin now also asserts the AudioPlayerPlayDirective on
the success path, closing the append-without-launch gap); F5 APPLIED into JF-720 (the
SessionQueue.AppendUnseen helper extraction covering the three hand-rolled
copy+dedup+assign idioms, with the roster pin asserting all three sites route through
it). Affected classes 86/86 both TFMs after the tail; independent suite 4969/4969 both
TFMs on the worker commit; merged-tree follows the merge.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (plugin project 0 errors / 0 warnings on the final state, both TFMs)
- [x] #2 dotnet test passes (final full suite `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`: 4969/4969 net9.0 AND net10.0; baseline 4962 + 7 new pins)
- [x] #3 No new compiler warnings introduced (grep-clean build/test output; the only warning ever seen was the pre-existing xUnit1030 in VideoAudioControllerTests)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A: no session-attribute shape touched)
- [x] #5 HttpClient instances not shared across calls modifying BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU fixtures updated if interaction model changed (N/A: no interaction model change; the refusal reuses the JF-687 StreamTokenNotConfigured pipeline translation)
- [x] #7 E2E added for new handler logic (N/A per the JF-699 precedent: the empty-secret refusal state is not safely reachable on the live server; the behavior is pinned by the 7 unit pins in PlaybackNearlyFinishedRefusalTests with red proofs)
- [x] #8 Locale strings in all 17 locales (N/A: no user-facing strings; the event-request refusal is the speechless keep-alive the pipeline already owns)
- [x] #9 /simplify passed (4 agents: reuse/simplification/efficiency/altitude; 6 findings applied incl. the derive-pair fold and the PendingContinuation record, 2 skipped-with-reason findings filed as JF-720, efficiency clean; dispositions in the Final Summary)
- [x] #10 /code-review high passed (5 findings, ALL applied in-code incl. the double-append race fix + its new pin; dispositions in the Final Summary)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 343c478f + gate-marker tail cdcfd283, merged as 35198e5b. The derive-then-commit reorder: both UpdateRecoveryPointer sites moved after the launch build (compensation rejected on three source-verified facts), the three exhaustion arms derive-only with a PendingContinuation record committed at one post-build point, and the pre-build exceptions marked with their true (not overclaimed) invariants. A refused continuation leaves the pointer naming the finishing item with position intact at every arm; the double-append race the reorder would have introduced was caught by the worker's own code review and fixed with commit-side re-dedup plus its race pin; the RadioModeTests fixture that had been passing via the phantom append was repaired to the real success path. 7 new pins, red proofs on both TFMs (pointer/commit/append sabotage each flipping their pins). Worker gates green (simplify 6 applied, 2 filed as JF-720; code-review high all 5 applied). Gate-marker verified all five scrutiny axes at source level; its 5 findings all landed (the false-invariant comment corrected, the lost-update KNOWN RACE note, the bounded derive-on-refire cost note, the success-path directive assert, and the AppendUnseen helper extraction folded into JF-720's scope). Suites: worker and orchestrator independent 4969/4969 both TFMs, merged-tree 4978/4978 both TFMs as concurrent split-TFM jobs. Production surface changed (PlaybackNearlyFinishedEventHandler): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Design Decision (written BEFORE coding, per the task mandate)

**Chosen: (a) derive-then-commit.** Both arms (and the exhaustion arms feeding them) keep
their derivation exactly where it is, and every state write moves AFTER the launch build,
committing only on a successful build. Reasons, verified against the merged tree at
dfe5e09e:

1. **Compensation leaves a real crash window.** The device-queue persist is debounced 2s
   with the payload captured at ARM time (JF-449's KeyedOneShotDebounce), so a
   compensate-on-refusal rollback that runs after a MoveTo can already have had the
   post-move phantom state persisted to the queue JSON (the atempo/transcode resolve plus
   the builder call sit under the shared 6s retry budget, well past the debounce). A crash
   inside that window reads exactly the phantom state this task exists to eliminate;
   derive-then-commit never writes it at all.
2. **A faithful rollback must bypass the pointer's ONE writer.** Restoring the pre-move
   pointer requires restoring `CurrentItemWrittenAt` (the JF-619 freshness-arbitration
   stamp) to its old value, but `DeviceQueue.SetCurrentItemPointer` always stamps
   UtcNow; the compensation would hand-assign the fields, breaking the "written
   exclusively through SetCurrentItemPointer" contract on DeviceQueue.
3. **Compensation covers only the typed refusal.** Any other failure between the write and
   the build would still leave the phantom. Derive-then-commit makes the invariant
   structural: no successful build, no commit.
4. **The reorder hazard the task warns about ("the build CONSUMES the item the pointer
   names") is verified ABSENT at these sites.** The build consumes the resolved
   `source`/`itemId` locals; `BuildAudioPlayerResponse` reads the device id, the
   launch-scope maps (`RecordLaunchBase`, `RecordEnqueue`) and the context token via
   `MintStreamToken`'s active-audio flag, never `CurrentIndex`/`CurrentItemId`/
   `CurrentPositionTicks`. The derivation (`ResolveNextItemId`) reads the SESSION queue
   view plus the event token, not the device pointer. Moving `UpdateRecoveryPointer`
   after the build therefore cannot change what gets built or enqueued.
5. **The reader contracts hold under the new order.** JF-579
   (`TryRehydrateSessionQueueFromDevice`) runs at handler ENTRY and mirrors `ItemIds`
   membership, which neither `MoveTo` nor `SetCurrentItemPointer` mutates: unaffected.
   The JF-691 enqueue record (`RecordEnqueue`) is written by the builder INSIDE the
   build, i.e. now BEFORE the handler-side commit rather than after it; its reader
   (`PlaybackFinishedEventHandler.EnqueuedForThisBoundary`) runs on a later event, when
   both writes have long landed, and needs no ordering between them. The JF-447
   "directive-time truth" property the stop classifier relies on (pointer names the
   enqueued item strictly before the displaced stream's stop can arrive) is preserved:
   the commit still runs before `HandleAsync` returns, i.e. before Amazon receives the
   directive, which is the earliest a displaced stop event can exist.

**Commit set (all deferred until after a successful build):**
- The device-queue pointer at both arms: the main continuation arm's `UpdateRecoveryPointer`
  (~262) and the precompute cached-enqueue arm's (~155).
- The session-queue appends + `RadioModeState.Enable` of the exhaustion arms:
  `AutoPopulateRadioTracks` / `AutoPopulatePostPlayTracks` (~203/~221) become
  derive-only and return a pending population the tail commits post-build, and
  `TryAutoAdvanceNextEpisodeAsync`'s single-item append (~911) joins them (the third
  append site in this handler; the same phantom class, treated uniformly so the invariant
  is "every SYNTHESIZED-continuation write in this handler commits after a successful
  launch build"; the pre-build writes that are not synthesized continuation are the
  marked exceptions below).

**Deliberately NOT moved:**
- `QueueContinuationStore.Remove` in the exhaustion block (~209): the removal states a
  fact about the SOURCE queue (every batch fetched), not about the enqueue; moving it
  after the build would resurrect a spent continuation on refusal and re-fetch an
  exhausted source forever.
- `TryFetchContinuationBatch`'s session-queue appends (~131): the fetched batches ARE the
  derivation input (`ResolveNextItemId` resolves the successor from them; the fetch must
  also run before the precompute early return, JF-666), and the items belong to the queue
  the user asked to play, not a synthesized continuation. A refusal leaves them as the
  queue's real future, which the next NearlyFinished re-serves once the secret is fixed.
- The entry rehydration and the sleep-expiry gate: they precede any derivation and are
  untouched.

**Pin plan:** a refused continuation (empty secret + a rate-continuity atempo launch,
seeded via the launch-scope store) leaves the device pointer naming the finishing item
with its position intact, CurrentIndex unmoved, no enqueue record, and (per arm) the
session queue not appended and radio mode not armed; a successful continuation still
advances the pointer exactly once and records the enqueue; all existing continuation pins
stay green unchanged.
<!-- SECTION:NOTES:END -->
