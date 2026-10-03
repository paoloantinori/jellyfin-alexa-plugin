---
id: JF-723
title: >-
  JF-713 derive-to-commit window: TrimLaunchBaseIfNeeded judges the fresh
  launch-scope entry against the OLD queue's membership
status: Done
assignee: []
created_date: '2026-10-03 02:12'
updated_date: '2026-10-03 22:13'
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
- [x] #1 dotnet build passes with 0 errors (full solution, both TFMs; the CS8600 warning the first builds emitted at VideoAudioControllerTests.cs(1680,35) was pre-existing on base 3d0c6563 on BOTH TFMs (the original evidence note wrongly said net9.0-only; the gate-marker round corrected it), in the JF-726 backstop test this diff never touched, and is FIXED on main by 770fc94f, which this branch carries after its rebase)
- [x] #2 dotnet test passes (full suite on the final state after the rework round: both TFMs; counts in the Final Summary)
- [x] #3 No new compiler warnings introduced (the only emitted warning was the pre-existing site above; post-rebase builds are warning-free)
- [x] #4 N/A: no session attributes touched (DeviceQueueManager launch-scope maps and one comment only)
- [x] #5 N/A: no HttpClient changes
- [x] #6 N/A: no interaction model change
- [x] #7 N/A: no new intent or handler logic; the coverage is the 5 unit pins (the JF-713 house pattern: the handler path is DB-coupled and gets pinned at the DeviceQueueManager level, mirroring the exact calls the arms make)
- [x] #8 N/A: no user-facing strings
- [x] #9 /simplify passed (4 angles: reuse 2 findings, simplification 4, efficiency CLEAN with measured evidence, altitude CLEAN with a seam/mechanism/scope adjudication; 5 deduped findings applied - the seeding helper SeedQueuedScopeResidents, the MinutesToMs idiom, the redundant call-site comment dropped, the AlbumPlayService breadcrumb shortened, the two long test docs trimmed; consciously kept with reasons: the slot-reuse inline comment, the const-doc density, the hardcoded 200)
- [x] #10 /code-review high passed (5 findings: 4 applied - the ENQUEUED-arm pin added, the JF-723 narrative doc relocated onto TrimLaunchBaseIfNeeded itself where the crefs point, TrimPositionMap's false "any key format" sentence replaced with an explicit FORMAT CONTRACT note, the banned parenthetical-hyphen prose forms rewritten; 1 partially applied - pin 4's BCL slot-reuse reliance is now documented with the format-independent anchors named, the mechanism left as-is because the shape is inherently slot-dependent and its degradation mode is vacuous green, not flaky red. The pending-pin addition exposed and fixed a seeding flaw in the first draft: the residents must be PENDING entries or the four-map OR gate never fires)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including a rework round: worker commits 4bdbe86f/ed598909 + rework 42c4fc31 (rebased onto the CS8600 fix 770fc94f), merged as e72b8234. The derive-to-commit window's launch-scope entry protected: RecordLaunchBase passes the just-written N key into the trim's queued set (option B on read evidence - both playlist arms build-before-commit by deliberate policy, and the record cannot move to the commit side without breaking the builder chokepoint). TWO load-bearing discoveries: the queued-membership protection INERT in production (the maps N-keyed, every SetQueue caller dashed - the hazard reachable today on a saturated map) filed as JF-738 with the corrected trim mechanism; and the sibling-trim hole (a sibling RecordLaunchBase in the window trims without the exemption; JF-738's fix cannot close it) filed as JF-739 with the fresh-stamping shape. Five const-seeded pins (MaxLaunchBaseEntries on the InternalsVisibleTo seam - a cap change can no longer degrade them to vacuous green), four red proofs mechanically reproduced by the orchestrator reviewer (who also caught the orchestrator's own CS8600 re-breaking CI, fixed as 770fc94f, green run 37155718492). Worker gates green on both rounds (the rework's gate-skip justified: reviewer-specified mechanical edits only); the orchestrator gate-marker verified all five axes including the guard mechanics and the option-A lineage at the cited lines, its 4 findings landed via the rework (JF-739 filed, the seam, the determinism note, the corrected warning claim). Suites: worker 5073/5073 post-rework both TFMs; orchestrator independent 5073/5073; merged-tree 5078/5078 both TFMs exit 0 on both split legs. Production surface changed (DeviceQueueManager): deployed in the batched post-closure deploy with JF-727.
<!-- SECTION:FINAL_SUMMARY:END -->
