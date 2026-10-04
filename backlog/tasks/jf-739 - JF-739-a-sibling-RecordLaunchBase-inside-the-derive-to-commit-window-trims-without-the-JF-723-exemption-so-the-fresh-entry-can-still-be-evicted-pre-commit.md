---
id: JF-739
title: >-
  JF-739 - a sibling RecordLaunchBase inside the derive-to-commit window trims
  without the JF-723 exemption, so the fresh entry can still be evicted
  pre-commit
status: Done
assignee: []
created_date: '2026-10-03'
updated_date: '2026-10-04 09:52'
labels:
  - playback
  - queue
  - launch-scope
dependencies:
  - JF-723
references:
  - >-
    backlog/tasks/jf-723 -
    JF-713-derive-to-commit-window-TrimLaunchBaseIfNeeded-judges-the-fresh-launch-scope-entry-against-the-OLD-queues-membership.md
  - >-
    backlog/tasks/jf-738 -
    queued-membership-protection-in-the-bounded-map-trims-is-inert-maps-are-N-keyed-queue-ItemIds-are-dashed.md
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
ESCALATION NOTED BY THE JF-738 CODE-REVIEW ROUND (2026-10-04): restored
membership makes the second bullet DETERMINISTIC in the queued-cap-pressure
shape JF-738 itself made production-reachable (a fully-navigated 200+-track
queue pins the map at cap with every entry queued, so the fresh un-committed
entry F is the ONLY non-queued key and any sibling trim needing one removal
evicts it with certainty, where pre-JF-738 the all-keys-non-queued FIFO hit
F only via rare free-list slot reuse). The reachability bar is still the
cap-pressure plus in-window-sibling conjunction below, but the conditional
eviction became an unconditional one inside that shape.
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

## Design decision (2026-10-04, BEFORE implementation)

CHOSEN SHAPE: (a) realized as the filing's fresh-stamping candidate - an
in-memory, per-device RECENT-RECORD STAMP REGISTRY on DeviceQueueManager
(deviceId -> {"N" map key -> UtcNow stamp}), guarded by the existing
`_launchScopeLock` (every stamp write and every trim-side consultation already
sits under that lock: `RecordLaunchBase` and `RecordStoppedPositionAndTrim`
both lock, and both trim families run inside those critical sections). Both
WRITE families stamp (`RecordLaunchBase`, including its two short-circuit early
returns per the JF-619 relaunch precedent, and `RecordStoppedPositionAndTrim`);
both TRIM families consult through ONE wrapper
(`BuildTrimMembershipSetWithFreshness` = `BuildTrimMembershipSet(queue,
freshKey)` UNION the device's unexpired stamps), so the freshness half is
structural, not convention. The window is `RecentRecordFreshnessWindow`
(internal, settable, default 5s: the derive-to-commit window is sub-second and
5s covers launch-build latency with margin; settable is the deterministic test
seam - TimeSpan.Zero = every stamp instantly aged - which keeps the four
existing aging pins' exact pre-JF-739 semantics). No persisted shape change
(the registry is in-memory by design; a restart outlives any window).

Why the orchestrator's shape (a) wording ("the sibling trim passes the PENDING
entry's key as the freshKey into the same builder") lands as a registry: the
sibling call runs on another thread and cannot KNOW the pending key as a
parameter; the registry is the mechanism that carries the pending key into the
sibling's membership build, reusing `BuildTrimMembershipSet` unchanged inside
the wrapper.

Shapes REJECTED:
- (b) "the trim moves after the commit point": fails on two grounds.
  `RecordLaunchBase` is the universal BuildAudioPlayerResponse chokepoint while
  commits (SetQueue/CommitShuffledQueue) happen only on queue-carrying paths,
  so single-item plays and enqueues would never trim (unbounded maps); and it
  does not even close THIS hole - the SIBLING's own trim still runs inside the
  window and can evict F; only suppressing every in-window trim (shape (c))
  would help, not reordering our own.
- (c) "the derive-then-commit window defers the trim": requires declaring the
  window as a scope threaded through every play path (~15 launch sites) with
  exception safety (a refused launch must still flush the deferred trim or the
  maps grow unbounded) and process-global suppression state on the shared
  manager (a stuck window disables bounding for every device); heavier and
  more fragile than the protection the hole needs, which the time-bounded
  stamp delivers with zero call-site changes.

DoD #2 DECISION: the ItemPositionState trim's window guard is ADDED, not
declined. The position store has the identical sibling shape (two concurrent
stops on one device; at queued cap pressure the first stop's fresh entry is
the only non-queued key and a sibling `RecordStoppedPositionAndTrim` evicts
it, losing the JF-581 resume seed), the fix is the same three lines (stamp the
written key, consult the shared wrapper), and the filing itself asks for "one
coherent freshness rule" across the two trim families.

Over-cap tolerance (documented in code): fresh entries are unevictable for the
window, so a map can sit at cap + (writes per window) during a burst
(displacement storm); bounded by the write rate, transient, and the same
tolerance class the JF-723 guard already grants at cap+1.

## Rework note (the /simplify + /code-review rounds, 2026-10-04)

The design section above was written before the gate rounds and is SUPERSEDED
on one axis: the freshness seam. As first shipped the window was an internal
settable TimeSpan with TimeSpan.Zero as the deterministic "every stamp
instantly aged" seam, and the four pre-existing aging pins were zeroed to keep
their pre-JF-739 semantics. The /simplify altitude and reuse rounds both
flagged that disable-style seam (the repo's established answer is the
NextTrackPrecomputeCache.Time TimeProvider seam, JF-424.2, and it left NO pin
proving expiry under the rule ON, which is exactly what the documented
over-cap-tolerance bound rests on). SHIPPED SHAPE: the window is now
`internal static readonly TimeSpan RecentRecordFreshnessWindow` (5s), the
manager carries `internal TimeProvider Time` (instance seam, production
always TimeProvider.System), a shared TestHelpers.FakeTimeProvider serves the
pins, and all four aging pins now ADVANCE the fake clock past the window
before their aging record - expiry under the rule ON, no sleeps, no disabled
mechanism. The three within-window pins freeze the fake clock. Do NOT "fix"
the aging pins back to zero-window or pre-JF-739 semantics: the advance IS
the aging proof.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Fresh entries are exempt from sibling trims within the launch window (stamp or equivalent), with a red-under-old-shape pin driving the change (the seed shape above)
  Evidence: three pins, all RED-PROVEN live against the unmodified base on BOTH TFMs before the fix (Failed: 3 / Failed: 3, each at the exact sibling-eviction assert: active arm Expected 1200000 got null; enqueued arm Expected 540000 got null after the promotion no-oped; position arm Expected 1800000000 got null), then green under the fix. DeviceQueueManagerTests: RecordLaunchBase_SiblingRecordInDeriveToCommitWindow_FreshEntrySurvivesToCommit_JF739 (includes the commit step), RecordLaunchBase_EnqueuedFreshPair_SurvivesSiblingActiveTrimInWindow_JF739, RecordStoppedPositionAndTrim_SiblingStopInWindow_FreshPositionSurvives_JF739.
- [x] #2 The ItemPositionState trim's window-guard decision made in the same sitting (added, or consciously declined with the reason recorded here)
  DECISION: ADDED. RecordStoppedPositionAndTrim stamps its written key and its trim consumes the shared membership build; the sibling-stop pin above red-proves the shape (two concurrent stops, first fresh entry the only non-queued key, JF-581 resume seed otherwise lost). One registry, both write families stamp, both trim families consult.
- [x] #3 dotnet build passes with 0 errors, no new warnings
  Evidence: Release --no-restore -warnaserror: 0 warnings, 0 errors, both TFMs, run after the simplify rework on the final state.
- [x] #4 dotnet test passes both TFMs
  Evidence: full suite ONCE on the final state: 5110/5110 net9.0 (1m38s) and 5110/5110 net10.0 (1m39s), baseline 5107 + 3 new pins. Filtered affected classes (DeviceQueueManagerTests + EventHandlerTests) 173/173 both TFMs re-run green after every edit round. Honest note: the FIRST full-suite run red once on net10.0 in VideoAudioControllerTests.Dispose (the JF-731 encode-backstop, a surface with ZERO references to this diff), did not reproduce (266/266 in isolation; full net10.0 suite 5110/5110 on re-run), transient under dual-TFM load.
- [x] #5 /simplify + /code-review high passed
  /simplify: 4 agents (reuse/simplification/efficiency/altitude), findings deduped and APPLIED: the two membership builders folded into ONE instance BuildTrimMembershipSet (deleting the wrapper name and the bypass hazard; TrimPositionMap's FORMAT CONTRACT now points at the builder that actually carries the guards); the recent-record seam converted from the settable window to the TimeProvider shape (shared TestHelpers.FakeTimeProvider; aging pins prove expiry rule-ON, see the rework note); Clear reclaims the device's stamp registry entry (the production-dead empty-dict sweep deleted); the position pin reuses SeedStoredPosition; doc restatement trimmed where pure duplication. SKIPPED with reasons: membership capacity hint not widened by the stamp count (cold over-cap path already O(queue-length); contorting the builder for one rehash); two UtcNow fetches per write+trim cycle (nanoseconds); doc restatement only partially trimmed (the repo's verbose mechanism-doc convention keeps the story at its canonical homes). /code-review high: 5 findings, ALL in scope, ALL applied: (F3) Clear's second lock acquisition could erase a stamp written by a concurrent record that re-created the queue - reordered to remove stamps UNDER the lock BEFORE the queue TryRemove, with the interleave analysis on the comment; (F1) the field doc's "holds only window-span records" overclaimed for burst-then-silent devices - replaced with the honest per-device pin-until-Clear-or-exit bound and why no sweep timer; (F5) the over-cap tolerance doc understated the cross-family union - corrected to "stamped keys in the window, BOTH families" with the cross-family protection declared deliberate (no dedicated pin: a corollary of the one-registry design, not a load-bearing contract of its own); (F4) the FakeTimeProvider doc claimed a "hoist" while PreEnqueueOnStartTests' local fake remains - corrected to mirrors-not-replaced with the divergence reason (the local one restores the static NextTrackPrecomputeCache.Time seam); (F2) this task file's design section had gone stale on the seam axis - the rework note above. JF-745 went unused: nothing out of scope.
  ORCHESTRATOR GATE-MARKER ROUND (2026-10-04, four findings, ALL applied):
  GM-F1 (real, contradicts the one-freshness-rule claim): PromotePendingLaunchBase is the launch family's THIRD bounded-map write and it bypassed the stamp - the promote fires LONG after the enqueue's stamp expired, and CopySurvivingStores can carry the pending pair across a queue replace whose ItemIds lack the item, so the just-promoted ACTIVE scope was non-queued and immediately evictable by the next sibling trim. APPLIED: StampRecentRecord inside the promote's existing lock (after WriteActiveLaunchScope); the field and StampRecentRecord docs now name all THREE writers; new pin PromotePendingLaunchBase_AfterQueueReplace_SurvivesSiblingTrimInWindow_JF739 (seed cap pressure, E enqueued, clock past the window, queue replaced without E, promote, sibling trim) RED-PROVEN by temporarily disabling the promote stamp (Expected 660000 got null on both TFMs), restored, green.
  GM-F2 (pin gap): the enqueued-arm pin never re-asserted the seeded pending residents, so a regression evicting residents at that pressure passed green. APPLIED: the pin captures the seeded ids and asserts every pending resident survived the sibling trim (direct PendingLaunchBaseMs read, the no-public-pending-reader idiom).
  GM-F3 (seam hardening): Time was an unguarded internal settable on the production DI singleton. APPLIED the fail-fast shape: get-only `public TimeProvider Time` plus `internal SetTimeForTest`, which throws on a second assignment (a manager left on a fake clock would freeze stamp expiry for every device; per-test managers make a second assignment a fixture leak). All seven test sites moved to SetTimeForTest.
  GM-F4 (test hygiene): the shared FakeTimeProvider duplicated PreEnqueueOnStartTests' private clock. APPLIED as a partial hoist: the clock CORE (ctor/Advance/SetUtcNow/GetUtcNow) is the one shared TestHelpers.FakeTimeProvider (unsealed), and PreEnqueueOnStartTests' nested class is now a thin adapter deriving from it that adds ONLY the static NextTrackPrecomputeCache install/restore that seam needs - one clock implementation, the static-seam discipline stays local.
  Runs this round: affected classes (DeviceQueueManagerTests + EventHandlerTests + PreEnqueueOnStartTests) 192/192 both TFMs (191 + the GM-F1 pin); full suite and Release -warnaserror re-run on the final state (see below).
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Chosen shape: (a), the JF-723 filing's fresh-stamping candidate - an
in-memory per-device recently-record stamp registry on DeviceQueueManager
(`_recentRecordStamps`, guarded by the existing `_launchScopeLock`; both
write families stamp via StampRecentRecord: RecordLaunchBase on every launch
record including its unchanged-value short-circuits per the JF-619 relaunch
precedent, and RecordStoppedPositionAndTrim on every qualifying stop = DoD #2
ADDED; both trim families consult through the ONE instance
BuildTrimMembershipSet(deviceId, queue, freshKey), which folds the JF-738
queued-membership builder and the unexpired-stamp union into a single path so
no freshness-less membership can be consumed). Shapes (b) and (c) rejected
with reasons in the design section (b cannot close the SIBLING's own trim and
unbounds non-committing paths; (c) needs a declared window through ~15 launch
sites with exception safety and process-global suppression). The window is 5s
(static readonly RecentRecordFreshnessWindow; sub-second derive-to-commit
window plus launch-build latency with margin, far under the LaunchVsStopGrace
scale); time comes from the instance `Time` TimeProvider seam (the
NextTrackPrecomputeCache.Time precedent), faked by the shared
TestHelpers.FakeTimeProvider.

Red proof: the three pins above ran against the unmerged base BEFORE the
production change and failed on BOTH TFMs at the exact deterministic-eviction
assert (the queued-cap-pressure shape JF-738 made production-reachable:
every resident queued, F the only non-queued key, the sibling trim needing
one removal). Under the fix all three are green and the four pre-existing
aging pins (JF-723 x2, JF-738 flipped characterization, JF-738 handler-level)
now prove expiry with the rule ON by advancing the fake clock past the
window; the saturated-churn pin advances per record so its slot-reuse premise
still exercises the freshKey guard. The gate-marker round added a FOURTH red
proof: the replace-then-promote arm (GM-F1), red under a temporarily-disabled
promote stamp (Expected 660000 got null on both TFMs), green with it. No
regressions: 5111/5111 both TFMs on the final state (baseline 5107 + 4 new
pins); Release -warnaserror 0/0 both TFMs. The JF-741
NormalizeToMapKeyFormat contract is untouched (NormalizeToMapKeyFormat and
the builder's dashed-to-N normalization unchanged; the stamp keys are already
"N"-normalized at their only writer sites). Gates: /simplify (4 agents),
/code-review high (5 findings), and the orchestrator gate-marker round
(GM-F1..GM-F4) as itemized under DoD #5; commits carry the Gates marker.

CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit f9445165 + rework fbf2ead6, --no-ff; the promote stamp and the SetTimeForTest guard verified by direct orchestrator read), combined-tree suite 5116/5116 both TFMs, CI green, deployed in the batched post-closure deploy. The orchestrator gate-marker verified all six axes (the amend claim checked byte-empty) plus the four rework findings applied, the promote stamp being the third write path the first cut missed, with its own fourth red proof.
<!-- SECTION:FINAL_SUMMARY:END -->
