---
id: JF-738
title: >-
  JF-738 - queued-membership protection in the bounded-map trims is inert: the
  maps are "N"-keyed while queue ItemIds are dashed, so no entry ever matches
status: Done
assignee: []
created_date: '2026-10-03'
updated_date: '2026-10-04 08:14'
labels:
  - playback
  - queue
  - launch-scope
dependencies: []
references:
  - >-
    backlog/tasks/jf-723 -
    JF-713-derive-to-commit-window-TrimLaunchBaseIfNeeded-judges-the-fresh-launch-scope-entry-against-the-OLD-queues-membership.md
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

### Design decision (2026-10-04, before implementation): set-side "N" normalization at the trim

Shape (b) of the filing's option list, via ONE shared canonicalization
helper on DeviceQueueManager (`NormalizeToMapKeyFormat`, internal static
beside `TrimPositionMap`): each `queue.ItemIds` entry that parses as a
GUID is re-keyed `ToString("N")` (any input format: dashed, "N", "B",
"P"); non-parseable entries pass through raw so a non-GUID map key still
compares equal to itself. On top of it, ONE membership-set builder
(`BuildTrimMembershipSet(queue, freshKey)`: normalize + the JF-723-shaped
fresh-key guard, capacity-hinted) so the normalize-and-guard invariant
has a single definition. Both trim sites consume the builder:
`TrimLaunchBaseIfNeeded` (gate already passed, one set shared by the four
maps) and the PlaybackStopped position trim, which moved INTO the manager
as the ONE locked write-then-trim entry point
`DeviceQueueManager.RecordStoppedPositionAndTrim(deviceId, queue, itemId,
ticks)` (the code-review round's finding, then tightened by the
gate-marker round's GM-F1: the WRITE folds under the lock beside the trim,
because the write is a structural Add for a new key and a sibling stop's
unlocked write would throw inside the trim's map enumeration; the count
gate runs first inside the lock so the set build stays off the under-cap
happy path, one qualifying stop per track): the membership build runs
under `_launchScopeLock`, because the build ENUMERATES the live
`ItemIds` list that `Enqueue` mutates in place under that same lock (the
JF-578 intent-thread/event-thread interleaving); the first JF-738 cut
used a lazy projection into `TrimPositionMap`, whose enumerator version
check is a throw path the pre-JF-738 CopyTo-based HashSet construction
did not have.

Why not (a) normalize the STORED queue at SetQueue/CommitShuffledQueue:
far bigger blast radius for zero extra protection. The persisted-XML
shape changes (legacy files stay dashed, so the trim would STILL need
set-side normalization for old files); every dashed-string queue reader
breaks or must be rewritten (`MoveTo`'s `IndexOf`, `Enqueue` and
`ResolveInsertIndex`'s `ToString()` comparisons, PlaybackStopped's
`queueContradictsEventToken` `Contains`); and it violates JF-713's
verbatim-store contract on the shuffle commit. The stored queue's format
is a de-facto public shape (FollowMe re-stores `sourceQueue.ItemIds`
verbatim); the membership set is a private, per-trim transient. Why not
(c) both directions: the maps are already uniformly "N"-keyed at every
writer (`RecordLaunchBase`, `RecordItemPosition`, the PlaybackStopped
position write), so there is exactly ONE direction needing
canonicalization (queue ids -> "N"); a second direction would exist only
to serve a dashed-keyed map, of which there are none. Why not INSIDE
`TrimPositionMap` itself (this filing's scope-point-2 suggestion): its
`as HashSet<string>` fast path means internal normalization would either
apply only in the build branch (silently bypassed by any caller-passed
HashSet: the same inert-membership hole, now type-dependent and harder
to see) or must drop the fast path and re-normalize the same set on each
of the four launch-map calls (4x projection of a 200+-item queue at the
exact cap-pressure shape); and the JF-723 fresh-key guard already forces
set construction to be caller-owned at both sites
(`queuedItems.Add(freshlyRecordedKey)` / `.Append(freshlyWrittenKey)`),
so moving only normalization down would split the membership invariant
across two layers.

DoD #2 decision: the fresh-entry guard for the ItemPositionState trim is
ADDED (the JF-723-shaped exemption: the just-written stop-position key is
added to the membership set for THAT call's trim only, inside the shared
builder). Rationale: the
write-then-trim shape is identical to RecordLaunchBase's; membership
protects the stopped item only when it is QUEUED, and the documented
single-item shape (JF-424.1: "single-item plays have no queue for MoveTo
to succeed on, and resume-after-pause depends on it") leaves the fresh
entry non-queued and evictable by its own trim at cap pressure; this
store is the resume seed the JF-581 live incident made load-bearing
(UserData write loss). Cost: the builder's single `Add`; the over-cap tolerance
(cap+1 until the next write ages the entry) is the same documented shape
the JF-723 guard already grants. A long run of non-queued single-item
stops churns at cap+1, evicting exactly one aged entry per write, and a
fully-navigated queue pins the map at the QUEUE's LENGTH, NOT at the cap
(GM-F3 honesty, corrected from the first cut's "pins at cap" framing:
every queued item's entry stays, so a 500-track queue carries ~500
`string->long` pairs, tens of KB of persisted JSON scaling with the
queue, which is the documented contract, not a leak). Each over-cap
qualifying write on such a queue then pays an O(queue-length) GUID
normalization under the launch-scope lock (recorded in the builder's
honest-cost note); bounded by the write frequencies (a launch record, a
qualifying stop, minutes apart), microseconds at playlist scale. A cheap
bound was considered and DECLINED: caching the membership set per queue
version adds invalidation surface on a correctness-critical set (every
ItemIds mutation site) for a microsecond-scale cold-path win, and a
length-32 pre-check to skip `Guid.TryParse` saves little (parsing a
32-char hex string is already near the check's own cost).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The membership set and map keys share one format at both trim call sites (set-side "N" normalization or equivalent); the JF738 characterization test flips red and is updated to pin the restored contract (queued seed survives, non-queued victim evicted)
  Evidence: both trim sites build their sets through `DeviceQueueManager.BuildTrimMembershipSet` (normalize via `NormalizeToMapKeyFormat` plus the fresh-key guard): `TrimLaunchBaseIfNeeded` for the four launch-scope maps, and the position trim which moved into the manager as `DeviceQueueManager.TrimItemPositionState` (locked, gate-first). RED PROOF executed live before the flip: with the fix in and the old pin untouched, `RecordLaunchBase_DashedQueueMembership_QueuedSeedIsTheEvicteeAtCapPressure_JF738Characterization` failed on BOTH TFMs (`Assert.Null() Failure ... Actual: 0` at the queued-seed assert); the pin was then replaced by `RecordLaunchBase_DashedQueueMembership_QueuedSeedsSurviveAndAgedNonQueuedEntryIsEvicted_JF738` (all queued residents survive via `Assert.All`; the fresh non-queued entry survives its own trim via the JF-723 guard; the NEXT launch ages it into the evictee, so the trim still trims; deterministic, order-independent).
- [x] #2 A fresh-entry guard decision for the ItemPositionState trim (exemption added, or consciously declined with the reason recorded here)
  Evidence: ADDED (see the DoD #2 decision paragraph above: the JF-723-shaped exemption rides the shared builder; single-item plays leave the fresh entry non-queued and the store is the JF-581 resume seed). Pinned end-to-end by `PlaybackStopped_ItemPositionStateTrim_ProtectsQueuedResidentsUnderDashedMembership_JF738` (EventHandlerTests, through the real HandleAsync path), itself RED-PROVEN by temporarily reverting the membership wiring to a raw `queue.ItemIds` set (failed both TFMs, restored, green).
- [x] #3 dotnet build passes with 0 errors, no new warnings
  Evidence: `dotnet build Jellyfin.Plugin.AlexaSkill.sln` 0 warnings 0 errors; `dotnet build -c Release -warnaserror` 0 warnings 0 errors (both TFMs).
- [x] #4 dotnet test passes both TFMs
  Evidence: full suite ONCE on the final state: 5095/5095 net9.0 and 5095/5095 net10.0 (baseline 5093 + 2 new pins; the characterization was replaced in place). Filtered affected classes re-run green after every edit round (DeviceQueueManagerTests + EventHandlerTests, 170/170 both TFMs).
- [x] #5 /simplify + /code-review high passed
  Evidence: /simplify (4 parallel agents): duplicated inline comment APPLIED (deleted; the MEMBERSHIP doc paragraph carries the story), lost HashSet capacity hint APPLIED (capacity-hinted build, then folded into the builder), task-file missing inside-TrimPositionMap rebuttal APPLIED (Design decision), AudiobookPositionTracker.NormalizeKey near-duplicate FILED as JF-741 (out of surface). /code-review high: F1 the unlocked live-ItemIds enumeration (introduced throw path: the lazy foreach's version check vs the pre-fix CopyTo-based ctor) APPLIED (locked manager entry point), F3 membership+guard hand-rolled twice / convention-only format invariant APPLIED (the shared builder, named mandatory by TrimPositionMap's FORMAT CONTRACT), F2 the JF-739 escalation recorded in JF-739's filing (deterministic-only-non-queued-key note), F4 confirmed already filed as JF-741. Reviewer verdict on the fix itself: correct, premise verified at all writers, both new tests hand-executed deterministic. Gate-marker round (orchestrator, six named axes all PASS) returned four rework findings, ALL APPLIED: GM-F1 the write folded into the locked entry point (`RecordStoppedPositionAndTrim`), GM-F2 the locked `IsItemQueued` membership read for the contradiction check, GM-F3 the queue-length honesty in the docs and here (cheap bounds considered and declined with reasons), GM-F4 the cap moved beside `MaxLaunchBaseEntries` on the manager with the entry point defaulting to it; dispositions detailed in the gate-marker section of the Final Summary.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The queued-membership protection in the bounded-map trims is REAL: both
trim sites (the four launch-scope maps via `TrimLaunchBaseIfNeeded`, and
the ItemPositionState write+trim via the new locked
`DeviceQueueManager.RecordStoppedPositionAndTrim` entry point the
PlaybackStopped handler calls) build their membership sets through ONE
shared builder, `BuildTrimMembershipSet`, which re-keys the stored
queue's DASHED ids to the maps' "N" format
(`NormalizeToMapKeyFormat`: Guid.TryParse, any input format, raw
passthrough for non-GUIDs) and adds the caller's fresh key (the JF-723
identity-guard shape). The normalization shape and the rejection of the
stored-queue, both-directions, and inside-TrimPositionMap alternatives
are recorded in the Design decision above.

GATE-MARKER REWORK ROUND (2026-10-04, four findings, all applied):
GM-F1 the ItemPositionState WRITE stayed outside the lock while the trim's
map enumeration ran inside it (a sibling stop's structural Add on the same
device would throw InvalidOperationException inside the locked region, the
die-before-the-ack class): APPLIED by folding the write into
`RecordStoppedPositionAndTrim` (write + count gate + membership build +
trim as ONE locked unit; the debounced persist is scheduled by the manager
after the lock). GM-F2 the queue-contradiction check's
`ItemIds.Contains(cleanItemId, OrdinalIgnoreCase)` enumerated the live list
unlocked 60 lines above the fixed trim (the identical throw class against
Enqueue's in-place Insert): APPLIED via the manager's locked
`IsItemQueued(queue, itemId)` read, short-circuit ordering preserved so the
locked read only runs when the pointer already contradicts the token.
GM-F3 the "pins the map at cap" framing understated the restored-membership
growth margin: APPLIED in the docs and here; a fully-navigated queue pins
the map at QUEUE LENGTH (see the DoD #2 paragraph), and each over-cap
qualifying write pays an O(queue-length) normalization under the
launch-scope lock; the cheap-bound options (per-version membership cache,
length-32 pre-check) were considered and DECLINED with reasons above.
GM-F4 `MaxItemPositionStateEntries` lived on the event handler while its
sibling cap and the trim entry point live on the manager (an idiom break
its own doc cited): APPLIED, the const moved beside
`MaxLaunchBaseEntries` on DeviceQueueManager and the entry point's cap
parameter defaults to it; the pin-seeding seam is now one place
(EventHandlerTests seeds from `DeviceQueueManager.MaxItemPositionStateEntries`).

The JF-723 characterization pin flipped exactly as filed: red on the fix
(queued seed survived where the pin demanded its eviction, both TFMs),
then replaced by the protected-world pin (residents survive; a fresh
non-queued entry survives its own trim via the guard and is evicted once
aged by the next launch, proving the trim still trims). A new
handler-level pin covers the position-store arm end-to-end, red-proven
by reverting the call-site wiring.

JF-739 INTERPLAY (documented, NOT fixed here): restored membership does
not close the sibling-trim hole. Membership protects only items already
in the STORED queue; inside the derive-to-commit window the fresh item
is still absent from it, so a sibling `RecordLaunchBase` in that window
still judges the fresh entry non-queued and can evict it. The JF-738
code-review round additionally established that in the
queued-cap-pressure shape (which this fix itself makes
production-reachable) the fresh entry is the ONLY non-queued key, making
the sibling eviction deterministic rather than slot-luck; that
escalation note is recorded in JF-739's filing. The JF-723 guard itself
was re-verified green under the restored membership (its two
"N"-seeded queued-cap-pressure pins pass for the matching reason, and
the saturated-churn pin still exercises the guard through the
disjoint-membership route).

Fresh-entry guard for the position trim: ADDED (DoD #2), rationale in
the Design decision. Behavior note for scope point 4 (GM-F3-corrected):
a fully navigated queue pins the position map at the QUEUE's LENGTH, not
at the cap (tens of KB of persisted JSON scaling with the queue, the
documented contract); non-queued single-item churn rides at cap+1,
evicting one aged entry per write.

Out-of-scope gate finding FILED: JF-741 (the AudiobookPositionTracker
NormalizeKey near-duplicate of the canonicalization rule).

CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commits 445fb4b8 + rework 76390085, --no-ff; the locked RecordStoppedPositionAndTrim unit verified by direct orchestrator read), quad-merged tree suite as above (one pre-existing flake in isolation-green class on net9.0, full green net10.0); CI green on the pushed merge; deployed in the batched post-closure deploy. The orchestrator gate-marker's four rework findings all applied (the folded locked write unit, the locked IsItemQueued read, the queue-length honesty, the cap relocation). JF-741 filed by this task (the NormalizeKey near-duplicate); JF-739 carries the escalation note and is now IN PROGRESS on the merged base.
<!-- SECTION:FINAL_SUMMARY:END -->
