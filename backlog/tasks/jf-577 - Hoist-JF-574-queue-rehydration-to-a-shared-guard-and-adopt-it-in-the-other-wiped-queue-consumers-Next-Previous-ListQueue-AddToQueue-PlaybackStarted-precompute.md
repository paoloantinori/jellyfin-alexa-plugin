---
id: JF-577
title: >-
  Hoist JF-574 queue rehydration to a shared guard and adopt it in the other
  wiped-queue consumers (Next/Previous/ListQueue/AddToQueue/PlaybackStarted
  precompute)
status: In Progress
assignee: []
created_date: '2026-09-16 11:18'
updated_date: '2026-09-16 12:55'
labels:
  - reliability
  - refactor
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-574 /simplify altitude pass (2026-09-16): TryRehydrateSessionQueueFromDevice in PlaybackNearlyFinishedEventHandler repairs the restart-wiped session queue at the depth of ONE consumer, but the same wiped session.NowPlayingQueue state is mis-read by other consumers that all have access to DeviceQueueManager: NextIntentHandler (~line 78) and PreviousIntentHandler (~line 78) speak "no more tracks" while music still plays; ListQueueIntentHandler (~line 66) speaks an empty queue; AddToQueueIntentHandler (~line 189) replaces the surviving queue's membership basis with a one-item list; PlaybackStartedEventHandler's precompute path reads the session queue too. Proposed shape: hoist the guarded rehydration to the shared layer next to the mirror primitive (a SessionQueue.EnsureRehydrated(session, context, queueManager) or static on ProgressReporter beside MirrorQueueToSession), carrying the two-leg coherence guard (empty session queue AND persisted queue contains the codec-parsed playing token) and the rationale doc; NearlyFinished's call site collapses to one line and the other consumers adopt incrementally. Adopting rehydration in Next/Previous/ListQueue CHANGES behavior after a mid-playback restart (that is the point), so each adopter needs its own characterization + test. Scope: hoist + NearlyFinished re-route (behavior-neutral, lock with existing PlaybackRestartRehydrationTests), then per-consumer adoption with tests.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Session 2026-09-16 (implementation; status left In Progress, tree left uncommitted for the orchestrator's gates).

## Hoist (behavior-neutral, done first)

`ProgressReporter.TryRehydrateSessionQueueFromDevice(queueManager, session, context, logger, logLabel)` - public static beside `MirrorQueueToSession` in `Alexa/Handler/ProgressReporter.cs`. Chosen over SessionQueue because ProgressReporter already references every dependency the guard needs (DeviceQueue/DeviceQueueManager via the Playback usings, StreamTokenCodec, Alexa.NET Context, ILogger) while SessionQueue (Alexa/Playback) would have gained its first Alexa.NET-Request and ILogger dependencies; the mirror primitive it calls already lives there. Body moved verbatim from PlaybackNearlyFinishedEventHandler with the two-leg coherence guard (empty session queue AND persisted queue contains the codec-parsed playing token), the ToList member snapshot (SetQueue can replace the instance concurrently), and the rationale doc; the log lines are parametrized by caller label. NearlyFinished's private method is deleted; its call site is the one-line shared call. `PlaybackRestartRehydrationTests` 3/3 green on both TFMs before any adopter work.

## Adopter verdicts

- **NextIntentHandler: ADOPTED.** Guard called after the JF-564 medium refusal, before the queue read. Extra required step beyond the entry call: the handler gates on `session.FullNowPlayingItem == null`, which stays null on the wiped shape until the next server report, so "current" now resolves as FullNowPlayingItem-first with a token fallback scoped to the rehydrated shape only (`rehydrated && StreamTokenCodec.TryGetItemId(...)`); non-rehydrated requests keep byte-identical semantics. The dead local `previousToken` (assigned, never read) was removed because it dereferenced `FullNowPlayingItem!` and would have NRE'd on the new path. Device-queue authority (JF-447) untouched: the guard mirrors the persisted order and the scan advances through it; ordering/MoveTo paths unchanged.
- **PreviousIntentHandler: ADOPTED.** Same shape (guard at entry; token fallback for current only when rehydrated; loop compares the resolved id).
- **ListQueueIntentHandler: ADOPTED.** Guard at entry only; ctor gained the optional `DeviceQueueManager?` param (DI-injected singleton, existing 4-arg test call sites unaffected). No current-item fallback: with no now-playing item the handler lists from the queue head, its existing behavior for a populated queue, so the rehydrated queue is listed whole.
- **AddToQueueIntentHandler: SKIPPED (old path kept).** Two blockers. (1) Both-store coherence: the handler writes ONLY the session queue (no queueManager at all); after rehydration the session queue would hold [device items..., added song] while the device store we rehydrated from never learns the song, so the next restart silently drops it and MoveTo/pointer updates fail for it; landing the add in the device store would make this handler a NEW writer of the queue membership (today only the play paths call SetQueue), the exact double-write/coherence-loop risk the task flags. (2) The `session.FullNowPlayingItem == null` branch would STILL ReplaceAll-play the added song over the live stream even after rehydration, so a correct adoption needs entry guard + both-store append writer + token-based playing detection: three coupled changes, beyond the additive-at-entry contract. Recommend a dedicated task if the wiped-shape AddToQueue hijack matters.
- **PlaybackStartedEventHandler precompute path: SKIPPED (evaluate-only).** `TryPrecomputeNext`'s session-queue read is a performance-cache write, not user-visible: on the wiped shape it no-ops and PlaybackNearlyFinished's own guard (the JF-574 original) owns the user-visible resolution one event later. Post-restart PlaybackStarted fires only for streams that start after the restart, i.e. after a NearlyFinished enqueue that already rehydrated (leg 1 of the guard would fail there anyway). Adopting would add a device-store read on every track start for no correctness gain.

## Tests (red-first per adopter)

New suite `Jellyfin.Plugin.AlexaSkill.Tests/Handler/QueueRehydrationAdoptionTests.cs` (8 tests): Next characterized first (Empty on wiped session, green), then flipped (serves queue member [2]; rehydrated queue order + now-playing update asserted); Previous serves member [1]; ListQueue lists names instead of the empty line; plus stale-queue (coherence leg 2) and at-boundary (playing last/first item keeps Empty) pins per transport adopter. Every flipped test observed RED on both TFMs before its handler change; `PlaybackRestartRehydrationTests` unchanged and green.

## Verification

Full suite `dotnet test` (no --no-build): 3988/3988 passed on net9.0 AND net10.0. `dotnet build -c Release`: 0 warnings, 0 errors. Files changed: ProgressReporter.cs, PlaybackNearlyFinishedEventHandler.cs, NextIntentHandler.cs, PreviousIntentHandler.cs, ListQueueIntentHandler.cs, QueueRehydrationAdoptionTests.cs (new).

## Risks

The guard's exposure on intent paths mirrors the already-shipped NearlyFinished exposure (same legs, same source); ClearQueue wipes the device store too so a cleared queue cannot resurrect. ListQueue on the wiped shape lists the whole rehydrated queue including the playing track (no now-playing item to anchor "upcoming"); accepted as the handler's existing populated-queue behavior. DOD items 9/10 (/simplify, /code-review) intentionally left to the orchestrator's gates per the task instructions.
<!-- SECTION:NOTES:END -->
