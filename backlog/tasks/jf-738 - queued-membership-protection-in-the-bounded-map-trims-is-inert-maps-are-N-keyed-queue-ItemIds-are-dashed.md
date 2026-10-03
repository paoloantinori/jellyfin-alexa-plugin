---
id: JF-738
title: >-
  JF-738 - queued-membership protection in the bounded-map trims is inert: the
  maps are "N"-keyed while queue ItemIds are dashed, so no entry ever matches
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - playback
  - queue
  - launch-scope
references:
  - 'backlog/tasks/jf-723 - JF-713-derive-to-commit-window-TrimLaunchBaseIfNeeded-judges-the-fresh-launch-scope-entry-against-the-OLD-queues-membership.md'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 by the JF-723 worker (reserved number), discovered while
gathering the eviction-mechanics evidence for the JF-723 decision: the
"entries for queued items all stay" half of the bounded-map trim contract is
DEAD IN PRODUCTION, because the membership set and the map keys are in
different GUID string formats and can never be equal.

EVIDENCE (all read 2026-10-03, worktree on 3d0c6563):

- The four launch-scope maps are keyed `parsedItemId.ToString("N")`
  (DeviceQueueManager.cs, RecordLaunchBase line ~425; the DeviceQueue.cs
  property docs each say "itemId (\"N\" format)"); ItemPositionState is
  "N"-keyed too (RecordItemPosition, PlaybackStoppedEventHandler line ~221).
- Every production queue-membership writer stores DASHED `Guid.ToString()`
  ("D" format): AlbumPlayService (both SetQueue arms and the album path,
  `i.Id.ToString()`), CrossMediaFallback, PlayArtistSongsIntentHandler,
  PlayBookIntentHandler (all `i.Id.ToString()`), DeviceQueueManager.Enqueue
  (`itemId.ToString()`). FollowMeIntentHandler re-stores sourceQueue.ItemIds
  (dashed by origin).
- The trims build their membership set straight from `queue.ItemIds`:
  `TrimLaunchBaseIfNeeded` (`new HashSet<string>(queue.ItemIds,
  StringComparer.OrdinalIgnoreCase)`) for the four launch-scope maps, and
  `PlaybackStoppedEventHandler` line ~302 calls
  `DeviceQueueManager.TrimPositionMap(queue.ItemPositionState, queue.ItemIds,
  MaxItemPositionStateEntries)` directly. OrdinalIgnoreCase does NOT
  normalize dash format, so a 32-char "N" key is never equal to a 36-char
  dashed id; `TrimPositionMap`'s doc claimed "any key format; compared
  case-insensitively", which was false as written (the JF-723 code-review
  round replaced that sentence with an explicit FORMAT CONTRACT note; only
  the mechanism, not the doc, remains open here).
- CONSEQUENCE 1 (contract): the trim degrades to pure insertion/slot-order
  FIFO; a QUEUED item's entry is evictable exactly like any other. Pinned
  green-on-arrival by
  `RecordLaunchBase_DashedQueueMembership_QueuedSeedIsTheEvicteeAtCapPressure_JF738Characterization`
  (DeviceQueueManagerTests): at cap pressure the evictee is the first QUEUED
  seed, not the non-queued 201st record. That test is this task's red proof:
  a correct fix makes it fail (the seed must survive and the victim go).
- CONSEQUENCE 2 (severity): with membership inert, a saturated map churns by
  dictionary slot order, and .NET's Dictionary free-list reuse means a fresh
  insert can land in the just-freed LOWEST slot and be evicted by its OWN
  record's trim. Pinned by
  `RecordLaunchBase_SaturatedMapFreshInsert_SurvivesOwnTrimUnderDashedMembership`
  (was red before the JF-723 guard): so at 200+ distinct previously-launched
  items on one device, a NEW item's launch-scope entry could be
  dead-on-arrival (reads base 0 / rate identity).

INTERACTION WITH JF-723 (already shipped): JF-723's identity guard (the
just-recorded key is never its own trim's evictee) covers the fresh-entry
half of consequence 2 under BOTH membership behaviors, so FIXING THIS TASK
does not reopen the JF-723 window hazard. But note the inverse: this task's
fix is what makes the queued-cap-pressure shape (a fully navigated 200+-track
playlist pinning the map over cap) production-reachable at all, which is why
the JF-723 guard had to land first.

SCOPE OF THE FIX (decide consciously when picking up):

1. Normalize ONE side. Either build the trim's membership set in "N" (parse
   each ItemIds entry with Guid.TryParse and store ToString("N")); cheap, the
   set is only built when a map is over cap) or store/compare ItemIds in a
   canonical format (far bigger blast radius: persistence compat, MoveTo,
   Enqueue, every reader). The set-side normalization is the small change.
2. The same normalization must reach the PlaybackStoppedEventHandler
   ItemPositionState trim (it calls TrimPositionMap directly with
   queue.ItemIds). Consider doing it INSIDE TrimPositionMap (normalize any
   parseable GUID in queuedItems to both formats, or normalize to "N") so
   both call sites get it once; mind the JF-723 guard's reliance on the
   fresh key being addable to that same set.
3. Sibling gap to decide in the same sitting: the ItemPositionState trim has
   NO fresh-entry guard (the just-written stop position for the stopped item
   can self-evict at saturation exactly like consequence 2);
   PlaybackStoppedEventHandler would need the same "never the evictee of the
   write that just created it" exemption JF-723 gave RecordLaunchBase.
4. Behavior note: restoring membership protection means a 200+-item queue
   whose every item has an entry pins the map over cap indefinitely (the
   documented contract already tolerates this: TrimPositionMap only removes
   non-queued entries). Verify persist size stays sane on such a queue.

ALSO CORRECTED HERE (for the record): the JF-723 filing's mechanism sentence
("TrimPositionMap removes the newest non-queued entries") mis-modeled the
eviction direction; `keysToRemove.Take(toRemove)` removes the OLDEST
non-queued entries (enumeration/slot order). The reachable fresh-entry
shapes are (a) the fresh key being the ONLY non-queued entry under
working membership (queued-cap pressure) and (b) fresh inserts landing in
reused low slots under the inert-membership FIFO this task files.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 The membership set and map keys share one format at both trim call sites (set-side "N" normalization or equivalent); the JF738 characterization test flips red and is updated to pin the restored contract (queued seed survives, non-queued victim evicted)
- [ ] #2 A fresh-entry guard decision for the ItemPositionState trim (exemption added, or consciously declined with the reason recorded here)
- [ ] #3 dotnet build passes with 0 errors, no new warnings
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
