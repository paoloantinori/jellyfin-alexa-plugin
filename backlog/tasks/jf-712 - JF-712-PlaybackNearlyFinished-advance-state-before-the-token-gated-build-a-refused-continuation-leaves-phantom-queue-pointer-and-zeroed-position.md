---
id: JF-712
title: >-
  JF-712 - PlaybackNearlyFinished advances recovery/queue state before the
  token-gated build, so a refused continuation leaves a phantom pointer and a
  zeroed position
status: To Do
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
