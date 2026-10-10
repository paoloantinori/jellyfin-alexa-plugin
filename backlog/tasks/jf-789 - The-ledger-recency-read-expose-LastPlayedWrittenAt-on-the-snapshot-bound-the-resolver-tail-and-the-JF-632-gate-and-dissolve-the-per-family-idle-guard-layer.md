---
id: JF-789
title: >-
  The ledger recency read: expose LastPlayedWrittenAt on the snapshot, bound the
  resolver tail and the JF-632 gate, and dissolve the per-family idle guard
  layer
status: In Progress
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-10 11:11'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-785
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-06 from the JF-785 root-fix consideration (the JF-627 altitude
review's observation), same-turn filing rule: the fix was REJECTED inside
JF-785 for blast radius, and this task carries the implementable shape now
that the investigation is on record.

PREMISE CORRECTION (the JF-785 finding): the missing primitive already
exists. `DeviceQueue.LastPlayedWrittenAt` (JF-619) is stamped by
`RecordLastPlayed` on every write, including the short-circuit relaunch path
(a relaunch refreshes the stamp deliberately), and it PERSISTS with the queue
(null on pre-JF-619 files, which the JF-619 resolver already treats as the
legacy tie). What is missing is only the read side:
`GetLastPlayedSnapshot` returns (itemId, route) without the stamp, and no
consumer bounds anything by recency.

THE WORK, in dependency order:

1. Expose the stamp: extend `GetLastPlayedSnapshot` (and the ONE
   `PlaybackLaunchBuilder.ReadLastPlayedSnapshot` wrapper) to return
   `LastPlayedWrittenAt` beside the pair. Watch the JF-626 honest-bounds note
   on the snapshot method: the pair is already non-atomic against the write
   side; a three-value return keeps the same single-lookup shape.
2. Pick the window and its null policy. No device evidence backs any number
   today; candidates: a playback-session-scale window (30-60 min) for "the
   device is still in a skill-launched session", with pre-JF-619 null stamps
   resolving EITHER as legacy-tie (unbounded, old files keep today's
   behavior) or stale (refuse). The legacy-tie choice is the conservative one
   and matches how JF-619's resume arbitration already treats null stamps.
3. Bound the resolver tail: `ResolveCurrentPlayingItem`'s final
   non-displacement fallback (`ResolveId(lastPlayedId)`) answers only within
   the window. This changes the DELIBERATE unbounded stances: RateItem
   (JF-626: rate-this on an idle device rates the days-old item), Repeat,
   SetPlaybackSpeed (both unguarded riders of the same tail). Each needs its
   own red proof, and any pin asserting the tail's days-old answer must be
   updated deliberately, not silently.
4. Bound the JF-632 medium gate: `ResolveScreenOwningMedium`'s belt treats an
   unresolvable or same-item VideoApp-routed entry as screen-owning
   regardless of age; with a recency read the belt can let an old entry
   classify as nothing. The gate's shipped comment records the tradeoff and
   warns bounding "would fork the family's ledger semantics" (loop/sleep/
   speed refusal-vs-no-media flips on idle devices); that fork is exactly
   this step and must be decided consciously, with the refusal strings' dead
   states re-examined.
5. Only then dissolve the guard layer: with a bounded tail, the JF-629 idle
   guards (HasCurrentPlaybackEvidence at four call sites) and the
   allowLedgerTailAnswers parameter (JF-785 Leg A) become redundant with the
   bound and can fold into the resolver's own recency check. The Leg B
   boundary pins (PlaylistEditIntentHandlerTests, JF-785) must be revisited:
   a bounded VideoApp-ledger evidence leg may become CORRECT where the
   unbounded one was rightly refused.

WHY IT WAS NOT DONE IN JF-785 (the recorded rejection): steps 3 and 4 change
documented deliberate behavior of families outside that task's scope (RateItem's
JF-626 stance, the JF-632 gate's KNOWN TRADEOFF), each needing its own red
proofs and likely a device round for the refusal-vs-no-media flips; the
window value has no device evidence; and the null-stamp policy is its own
decision. JF-785 shipped the guard-layer unification instead (all four
families on the ONE predicate plus the tail rejection), which shrinks but
does not remove this task.

VERIFICATION BAR: per changed family, a red proof on the unmodified tree (the
idle-days-old shape acting on the tail) and a green pin on the bounded shape;
the lockstep matrix (PlaybackLaunchBuilderMediumTests) and the JF-785 pins
(the tail-refusal pair and the Leg B boundary pair) re-run and deliberately
updated where the bound supersedes them.
<!-- SECTION:NOTES:END -->
<!-- SECTION:DESCRIPTION:END -->
