---
id: JF-688
title: >-
  JF-688 - residuals of the JF-683 Finished keep-alive: unreshuffled shuffle at the
  last queue position still ends the session mid-playback, and a
  deleted-from-library successor can keep the session alive over dead audio
status: To Do
assignee: []
created_date: '2026-09-30 21:40'
labels:
  - playback
  - progressive-queue
  - bug
dependencies:
  - JF-683
references:
  - >-
    backlog/tasks/jf-683 - JF-683-the-PlaySong-fallback-artist-queue-never-continues-silent-guard-skip-at-the-prefetch-window-no-fetch-no-log-PlaybackFinished-calls-queue-exhausted-2-tracks-early.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed same-turn as JF-683's code-review round (the review's findings 1 and 2,
declined in-task with reasons; every review recommendation lands somewhere the
turn it is read).

PlaybackFinishedEventHandler's JF-683 keep-alive arm answers "does playback
continue?" from (a) the Alexa context playerActivity, (b) a POSITIONAL successor
in the session queue, (c) RepeatOne/RepeatAll loop mode. Two shapes where
NearlyFinished's actual enqueue policy disagrees with that heuristic:

1. UNSHUFFLED SHUFFLE AT THE LAST POSITION. When the device queue says
   PlaybackOrder=Shuffle WITHOUT a physical reshuffle (OriginalItemIds null),
   PlaybackNearlyFinishedEventHandler.ResolveNextItemId picks a RANDOM next
   track (count > 1) even at the last position, so playback continues - but the
   Finished arm sees finishedIndex = Count-1 (no positional successor) and
   RepeatNone, and ends the session at the boundary: the JF-683 symptom
   (dismissed APL screen, next pause sessionNew=true) persists for this mode.
   Not fixed in JF-683 because the authoritative shuffle state lives behind
   ResolvePlaybackOrder, PRIVATE to PlaybackNearlyFinishedEventHandler: fixing
   it in place would duplicate that policy in Finished. The right shape is
   hoisting ResolvePlaybackOrder (or a "would playback continue?" predicate) to
   the shared ProgressReporter home both handlers already use, then consuming
   it from both ResolveNextItemId and the Finished arm.

2. DELETED-SUCCESSOR LINGERING SCREEN. When NearlyFinished's full resolution
   finds a next item that GetItemById cannot resolve (deleted from the
   library), it logs a warning and answers Empty: NOTHING is enqueued, the
   device stops after the current stream, yet the Finished arm still sees the
   positional successor and keeps the session/APL screen open over dead audio
   until Amazon reaps the session. Cosmetic and self-healing; fix if a cheap
   authoritative "what did NearlyFinished enqueue" record ever lands (the
   device queue pointer NearlyFinished advances is the closest candidate).

Both shapes are PRE-EXISTING behavior at base (the activity-only gate ended the
session in both); JF-683 fixed the common sequential shape and left these two
corners honest rather than half-fixed.
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
