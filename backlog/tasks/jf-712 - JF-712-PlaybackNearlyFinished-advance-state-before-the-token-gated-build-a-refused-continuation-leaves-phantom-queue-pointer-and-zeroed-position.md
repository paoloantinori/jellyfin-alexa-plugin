---
id: JF-712
title: >-
  JF-712 - PlaybackNearlyFinished advances recovery/queue state before the
  token-gated build, so a refused continuation leaves a phantom pointer and a
  zeroed position
status: Done
assignee: []
created_date: '2026-10-02 15:05'
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
<!-- SECTION:DESCRIPTION:END -->

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
Closed 2026-10-03 in the worker worktree (base dfe5e09e). DESIGN DECISION (written in
this file BEFORE coding, above): (a) derive-then-commit, not (b) compensate-on-refusal.
The compensation design leaves a real crash window (the device-queue persist is
debounced 2s with the payload captured at arm time, and the resolve+build path sits
under the 6s retry budget, so the phantom pointer can reach the JSON before any
rollback), would have to hand-assign pointer fields to restore the JF-619 stamp
(bypassing SetCurrentItemPointer's ONE-writer contract), and covers only the typed
refusal. Derive-then-commit never writes the phantom at all and holds for ANY build
failure. The task's warned reorder hazard ("the build consumes the item the pointer
names") was verified absent at both arms: the build consumes the source/itemId locals
and never reads CurrentIndex/CurrentItemId/CurrentPositionTicks; the derivation reads
the session queue + token; the JF-579 rehydration reader runs at entry and mirrors
ItemIds membership (untouched by MoveTo/SetCurrentItemPointer); the JF-691 enqueue
record is written inside the build (now before the handler commit rather than after;
its Finished-handler reader runs on a later event and needs no ordering); the JF-447
directive-time-truth property survives because the commit still runs before
HandleAsync returns, i.e. before Amazon receives the directive.

WHAT LANDED (PlaybackNearlyFinishedEventHandler): both UpdateRecoveryPointer calls
(main continuation arm + precompute cached-enqueue arm) moved strictly after the
launch build; the three exhaustion arms (radio, PostPlay AutoPlay, JF-324 episode
auto-advance) became derive-only, each returning a PendingContinuation (positional
record, FirstNewId computed with a structural empty-list guard) that the ONE commit
point (CommitPendingContinuation: session-queue append re-deduped against the live
queue + RadioModeState arming, then UpdateRecoveryPointer) commits only on a
successful build. The two former AutoPopulate helpers folded into one
DeriveSimilarTracksPopulation (the reorder had removed their last structural
differences; the JF-670 gate and dedup are now single-sourced). Deliberately NOT
moved, each marked in code: QueueContinuationStore.Remove (a source-queue fact;
moving it would resurrect a spent continuation), TryFetchContinuationBatch's appends
(the derivation input, JF-666), the entry rehydration. One pre-existing fixture was
repaired, not bent: RadioModeTests.PlaybackNearlyFinished_WithRadioMode_AutoQueuesSimilar
had passed via the phantom append itself (the successor's GetItemById was unmocked,
the old code appended before the item-not-found early return); it now mocks the
successor and pins the success-path append. One stale doc pointer the rename created
in PlayRadioIntentHandler was fixed.

THE PIN (PlaybackNearlyFinishedRefusalTests, 7 facts): a refused continuation (empty
secret + the JF-636 rate-continuity atempo launch seeded via the launch-scope store)
leaves the device pointer naming the finishing item with its position intact,
CurrentIndex unmoved, no enqueue record, the session queue un-appended, and radio
mode un-armed, at every arm (main, cached-enqueue, radio, PostPlay, episode); the
success-parity pin shows a non-refusing advance still moves the pointer exactly once
and records the enqueue; the multi-fire race pin (code-review F1) shows a sibling
fire committing the same track inside the derive-to-commit window cannot double-append.
RED PROOFS run: pointer-before-build restored -> the main-arm pin fails on both TFMs;
commit-before-build restored -> the three exhaustion-arm pins fail on both TFMs; the
commit's re-dedup reverted to blind AddRange -> the race pin fails on both TFMs.

GATES: /simplify (4 agents) applied 6 findings (derive-pair fold; PendingContinuation
record + computed FirstNewId; class-doc scoping to "every SYNTHESIZED-continuation
write" with the three named pre-build exceptions + the missing TryFetchContinuationBatch
BY DESIGN marker + this file's invariant sentence fixed; rationale restatements trimmed
from 11 sites to the class doc + pointers; test seeding/assertion helpers
SeedExhaustedScene + AssertPointerIntact; unused using + unread destructure) and
skipped 2 with reasons filed as JF-720 (the CreateNearlyFinishedRequest 5th-copy
graduation to TestHelpers; the writer-side IL roster pin); efficiency was clean.
/code-review high returned 5 findings, all applied: F1 the double-append race the
reorder itself introduced (the commit's read-modify-write vs the old derive-time
whole-list REPLACE's benign last-write-wins) fixed by the commit-side membership
re-dedup + its deterministic pin; F2 the FirstNewId structural guard; F3 the stale
PlayRadioIntentHandler doc pointer; F4 leftover misindentation; F5 the success-pin
config consistency (SetServerAddress). Suites: final 4969/4969 on BOTH TFMs
(`dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`, exit 0), 0 new warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
