---
id: JF-723
title: >-
  JF-713 derive-to-commit window: TrimLaunchBaseIfNeeded judges the fresh launch-scope
  entry against the OLD queue's membership
status: To Do
assignee: []
created_date: '2026-10-03 02:12'
labels:
  - playback
  - queue
  - launch-scope
dependencies:
  - JF-713
references:
  - >-
    backlog/tasks/jf-713 -
    JF-713-PlayPlaylist-shuffle-branch-commits-SetShuffledQueue-before-the-launch-build-derive-then-commit-via-snapshot.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-713 /code-review high round (finding 3 of 5),
carrying the one latent finding judged real but out of that task's scope.

MECHANISM: since JF-713 the playlist shuffle arm derives the snapshot, BUILDS the
launch, and only then commits the queue. BuildAudioPlayerResponse's launch-scope
capture (RecordLaunchBase -> TrimLaunchBaseIfNeeded, DeviceQueueManager.cs) runs
INSIDE that window, so it writes the fresh entry onto the OLD device queue and the
trim's queued-items set is the OLD queue's ItemIds, which does not contain the
freshly launched playlist item. At launch-scope cap pressure (MaxLaunchBaseEntries
= 200 per map) with enough older non-queued entries, TrimPositionMap removes the
newest non-queued entries, including the fresh one, before CopySurvivingStores
carries the maps into the committed queue. Pre-JF-713 the shuffled queue was
already stored when the builder ran, so the entry was queued and untouchable.

WHY NOT FIXED IN JF-713: the eviction is SEMANTICALLY INERT on every path that
exists today. The playlist launch records base=0 / rate=1000 (the static-stream
defaults; AlbumPlayService passes no launchBaseMs), and a missing scope entry
composes exactly like a zero base at identity rate. The hazard only materializes
for a future NON-ZERO-base playlist launch (for example a resume-aware or
speed-aware shuffle start) on a device already at the launch-scope cap. The
KNOWN RACE note at the AlbumPlayService commit site (JF-713) names this task.

CANDIDATE FIXES (pick one, with a pin that is red under the old-queue-membership
shape): (a) give SetCurrentItemPointer/RecordLaunchBase's caller a way to name the
incoming queue (the replace could be passed through, or the fresh item id could be
added to the trim's queued set for the duration of the launch); (b) fresh-stamp
launch-scope entries so the trim never evicts an entry recorded within the same
launch window; (c) move the playlist arm's RecordLaunchBase effect to the commit
side (derive-then-commit the launch scope too), mirroring the queue itself.

RELATED, same window (do NOT fix blindly): the commit's whole-list queue replace
is unlocked while sibling playback events may mutate the OLD queue during the
build; that dropped-write race is shared with the JF-699 ordered arm and is
documented as a KNOWN RACE at both sites. If a live bite ever demands the lock,
it belongs with the ReplaceQueue helper (extracted in JF-713) so SetQueue,
CommitShuffledQueue, and any future reset path take it together.
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
