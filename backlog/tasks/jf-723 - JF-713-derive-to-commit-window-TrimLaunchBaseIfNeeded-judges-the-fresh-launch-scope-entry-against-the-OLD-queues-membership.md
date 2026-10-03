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
DECISION: option (b), the identity guard - the key `RecordLaunchBase` just wrote
is never that same call's trim's evictee (`TrimLaunchBaseIfNeeded(queue, key)`
adds it to the trim's queued set). Rejected with evidence: (a) reordering the
trim after the commit would revert the deliberate JF-687/JF-699/JF-713
refusal-before-phantom-state ordering (every play path, both playlist arms
included, now builds the launch BEFORE `CommitShuffledQueue`/`SetQueue`; the
call sequence was read at AlbumPlayService.cs ~1024-1054 and
PlaybackLaunchBuilder.cs ~1985); (c) moving the record to the commit side would
break the `BuildAudioPlayerResponse` chokepoint that captures launches from
paths that never commit a queue (carousel taps, resume confirmations). The
guard is behavior-neutral on today's default playlist launches (base 0 / rate
1000 compose identically to a missing entry) and becomes load-bearing exactly
when a nonzero-base or non-identity-rate launch meets cap pressure.

EVIDENCE THE DECISION RESTS ON: the filing's inertness argument turned out to
be understated in both directions. (1) The eviction mechanism sentence in the
filing ("removes the newest non-queued entries") mis-models `Take(toRemove)`,
which removes the OLDEST non-queued entries; corrected in the JF-738 filing.
(2) A NEW bug was found and filed as JF-738: the trim's queued-membership
protection is inert in production because the four maps are "N"-keyed while
every production SetQueue caller stores dashed ids (OrdinalIgnoreCase does not
normalize dash format), degrading the trim to pure slot-order FIFO; under that
behavior a fresh insert reusing a freed low slot is evicted by its OWN record's
trim, so the JF-723 shape is reachable TODAY on a saturated map (200+ distinct
previously-launched items), not only after a future nonzero-base playlist
launch. The guard covers both behaviors by construction; JF-738's fix (which
makes the queued-cap-pressure shape production-reachable) therefore cannot
reopen this window.

PINS (5, DeviceQueueManagerTests; red proofs run with the guard line disabled,
4 of 5 red / JF-738 characterization green, then all green restored):
queued-cap-pressure survival of a nonzero-base ACTIVE entry; aging (the
previous fresh entry IS evictable by the next launch, so the guard never
disables trimming); the JF-738 mismatch characterization (green on arrival,
the red proof JF-738's fix will produce); saturated-map self-eviction survival
under dashed membership; and the ENQUEUED arm (a fresh PENDING pair survives
and promotes with its base and rate). The suite baseline held: 5073/5073 both
TFMs (5068 + 5), full solution build clean apart from one pre-existing warning
in the untouched JF-726 test file (net9.0 CS8600 at VideoAudioControllerTests
cs:1680, present on base 3d0c6563).

Gates: /simplify (4 parallel agents; 5 deduped findings applied, keeps
documented) and /code-review high (5 findings; 4 applied, 1 partially applied
as documented doc-strengthening). Production surface changed
(DeviceQueueManager guard + docs, AlbumPlayService comment): NOT deployed
(test-and-hardening change; no handler behavior change on default paths).

REWORK ROUND (gate-marker tail, 4 findings, all landed): F1 FILED as JF-739 -
the guard exempts SELF-trim only, so a sibling RecordLaunchBase interleaved in
the same derive-to-commit window (a PlaybackNearlyFinished enqueue, a
queue-editing launch) can still evict the fresh entry, and the JF-738 fix will
not close it because the fresh item is absent from the STORED queue until
commit; the filing carries the JF-723 option-(b) fresh-stamping fix shape and
its pin recipe, and the guard's doc phrase was corrected to the honest span
("treated as queued by THIS call's trim only", with the JF-739 pointer). F2
applied: MaxLaunchBaseEntries is now internal on the InternalsVisibleTo seam
(VideoAudioCache / KeyedOneShotDebounce pattern) and all five pins seed their
cap pressure from it, so a cap change can no longer degrade the pins to
vacuous green. F3 applied: the JF-738 characterization pin carries the same
Dictionary-order determinism note as the saturated-map pin, naming the two
format-independent anchors. F4 applied: the CS8600 evidence claim was
corrected (the warning was on BOTH TFMs, not net9.0-only; pre-existing in the
untouched JF-726 file, fixed on main by 770fc94f). The branch was REBASED onto
current main (770fc94f) to pick that fix up: post-rebase full-solution build
is 0 warnings 0 errors, the guard commit re-landed as the rework parent, and
the final state holds 5073/5073 net9.0 and 5073/5073 net10.0 (5068 + the 5
const-seeded pins) plus 5/5 filtered pin runs on both TFMs. Gate refresh on
the rework diff was judged unnecessary per the triviality bar: the rework
changes no production logic (the guard line is untouched; the const visibility
and two doc phrases are the only production deltas) and no test logic (the
pins now read the very constant the reviewer specified), and every edit
applies a reviewer-specified finding verbatim.
<!-- SECTION:FINAL_SUMMARY:END -->
