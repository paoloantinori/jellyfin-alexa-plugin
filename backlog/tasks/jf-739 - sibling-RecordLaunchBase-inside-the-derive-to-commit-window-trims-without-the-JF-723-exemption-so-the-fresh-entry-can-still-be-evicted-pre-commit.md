---
id: JF-739
title: >-
  JF-739 - a sibling RecordLaunchBase inside the derive-to-commit window trims
  without the JF-723 exemption, so the fresh entry can still be evicted
  pre-commit
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - playback
  - queue
  - launch-scope
dependencies:
  - JF-723
references:
  - 'backlog/tasks/jf-723 - JF-713-derive-to-commit-window-TrimLaunchBaseIfNeeded-judges-the-fresh-launch-scope-entry-against-the-OLD-queues-membership.md'
  - 'backlog/tasks/jf-738 - queued-membership-protection-in-the-bounded-map-trims-is-inert-maps-are-N-keyed-queue-ItemIds-are-dashed.md'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 from the JF-723 gate-marker rework round (finding 1; the
review-recommendation rule: the finding is real, out of the shipped change's
scope, and lands in the tracker the same turn). The JF-723 identity guard
exempts the freshly recorded key from its OWN record's trim only.

MECHANISM: on every play path (both playlist arms included) the launch build
runs BEFORE the queue commit (the JF-687/JF-699/JF-713
refusal-before-phantom-state ordering; AlbumPlayService builds at ~1024 and
commits at CommitShuffledQueue ~1048 / SetQueue ~1054). Inside that
derive-to-commit window, the launch-scope entry F for the freshly launched
item is written onto the OLD queue's maps. A SIBLING
`DeviceQueueManager.RecordLaunchBase` that interleaves in the same window (a
`PlaybackNearlyFinished` enqueue of the next track through the
BuildAudioPlayerResponse chokepoint, or a queue-editing launch such as
AddToQueue/PlayNext; the same event-thread/intent-thread interleaving class
the JF-713 KNOWN RACE note documents for dropped membership writes) runs its
own `TrimLaunchBaseIfNeeded`, which exempts only ITS key, not F. F's item is
still absent from the STORED queue, so nothing else protects F:

- under today's JF-738 behavior (membership inert, pure insertion/slot-order
  FIFO) F survives only while it is not the lowest-slot evictable entry (a
  freed-slot landing or an old-slot re-record makes it evictable);
- under the POST-JF-738 behavior (working membership) F is non-queued in the
  old membership and evictable whenever the sibling trim needs non-queued
  removals and F is old enough.

Either way the JF-738 fix does NOT close this hole: F is absent from the
STORED queue until the commit lands, which is the whole point of the window.
REACHABILITY: needs cap pressure on a launch-scope map plus a sibling launch
in a sub-second window plus F evictable in that sibling's trim; narrow, same
severity class as the JF-723 finding it generalizes (silent base 0 / rate
identity on a nonzero-base entry, no error anywhere).

CANDIDATE FIX (the JF-723 filing's option (b), fresh-stamping): entries
recorded within the current launch window are never evictable by ANY trim in
that span, not just their own. Recommended shape: an in-memory
recently-recorded ring (key -> record timestamp, consulted by
TrimLaunchBaseIfNeeded the way it consults the queued set, entries expiring
after a few seconds); the window is sub-second and in-process, so NO
persistence shape change is needed (the four maps' persisted shape stays
byte-compatible). Alternative: a persisted per-entry stamp (the
LastPlayedWrittenAt precedent, JF-693), which costs a fifth map or a value
shape change on all four; only worth it if the stamp ever needs to survive a
restart. SIBLING GAP TO DECIDE IN THE SAME SITTING: the ItemPositionState
trim in PlaybackStoppedEventHandler (~line 302) has neither a JF-723-style
self-guard NOR a window guard (a stop-position written in the window can be
evicted by any concurrent trim); JF-738 scope item 3 already names its
self-guard decision, and the window guard belongs with THIS task's stamp so
the two trim families get one coherent freshness rule.

PIN SHAPE for whoever picks this up: seed queued residents at cap (the
JF-723 `SeedQueuedScopeResidents` helper, DeviceQueueManagerTests), record
the fresh nonzero-base entry F (its own trim protects it), then record a
SIBLING new item G on the same device before any commit; red today when F's
scope reads null after G's record under the seeded eviction pressure, green
once the stamp covers F during the window.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Fresh entries are exempt from sibling trims within the launch window (stamp or equivalent), with a red-under-old-shape pin driving the change (the seed shape above)
- [ ] #2 The ItemPositionState trim's window-guard decision made in the same sitting (added, or consciously declined with the reason recorded here)
- [ ] #3 dotnet build passes with 0 errors, no new warnings
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
