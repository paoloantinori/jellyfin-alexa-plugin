---
id: JF-691
title: >-
  JF-691 - residuals of the JF-683 Finished keep-alive: unreshuffled shuffle at the
  last queue position still ends the session mid-playback, and a
  deleted-from-library successor can keep the session alive over dead audio
status: In Progress
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

## Design note (worker, 2026-10-01, both corners)

### Corner 1 (shuffle-random at the last position): mode-aware arm, resolver hoisted

`ResolvePlaybackOrder` moves verbatim from its private home on
`PlaybackNearlyFinishedEventHandler` to a public static
`ProgressReporter.ResolvePlaybackOrder(session, context, DeviceQueueManager?)`
(the `TryRehydrateSessionQueueFromDevice` precedent: per-call manager param on
the shared home both handlers already use); NearlyFinished calls the shared
member, deleting the private copy.

The Finished arm is the resolver's own admission condition mirrored, not a
blanket `Count > 1`: `order == Shuffle && !reshuffled && finishedIndex >= 0 &&
Count > 1`. That is EXACTLY the guard in front of ResolveNextItemId's
random-pick branch (including the index-found precondition), so the arm can
only fire when the resolver itself would have produced a next. In Default order
and in reshuffled-queue mode the last position is TRUE exhaustion (the resolver
returns null there), where the session must still end: a blanket widening would
have kept the session alive over real exhaustion in reshuffled mode. Joins the
existing JF-683 loop arm (RepeatOne/RepeatAll) as the second mode-aware pin.

### Corner 2 (deleted-successor lingering screen): the enqueue record + Finished veto

Chosen shape: an authoritative per-device record of the Enqueue directive the
skill actually issued, read as a VETO in Finished. NOT a session attribute:
AudioPlayer event requests carry no session at all, so Finished could never
read one (and JF-387 restricts attr copying to open-session responses anyway).
The legal home is the persisted per-device `DeviceQueue` (additive nullable
JSON properties, the `LastPlayedLaunchRoute` compat pattern):
`LastEnqueueAfterToken` + `LastEnqueueNextItemId`, written by ONE method
`DeviceQueueManager.RecordEnqueue` whose only call site is the
`PlaybackLaunchBuilder.BuildAudioPlayerResponse` chokepoint right beside the
`ExpectedPreviousToken` assignment (same `Enqueue && token != null` condition):
every Enqueue directive the device is told about lands in the record, so it
cannot drift from what was actually sent (NearlyFinished's two serve sites and
any other Enqueue caller covered by construction).

Finished applies it only where the queue-shape arms (successor / loop /
shuffle-random) say continue: when the injected queue manager holds a queue for
the device, the arms' keep-alive additionally requires
`queue.LastEnqueueAfterToken == req.Token`; a null or mismatched record ends
the session with a debug line naming the view. That is precisely the
deleted-successor shape: NearlyFinished returned Empty, nothing was enqueued,
the record still names the PREVIOUS boundary. Missing manager or missing queue
keeps the pure heuristic (nothing authoritative to consult); no
`Plugin.Instance` fallback on the read (hermetic tests; in production DI
injects the same singleton the chokepoint wrote).

Honest residual: the record persists on the debounced writer (~2s), and
NearlyFinished runs seconds before Finished, so a restart in that exact window
can veto a legit boundary; rare, self-healing (Amazon reaps, the next play
relaunches), and named by the debug line.

Pins: (a) shuffle-random keep-alive at the last position (RED at base, the
session ended), the reshuffled-queue negative (still ends), the
manager-less Default-order negative already pinned by JF-683;
(b) the end-to-end chain on the class manager (NearlyFinished on a
deleted successor leaves the record untouched, Finished then ENDS the session
instead of lingering: RED at base, it kept alive) plus the healthy-boundary
companion (record matches, keep-alive survives the veto).

### Gate rounds (worker, same turn)

/simplify (4 agents): APPLIED the merged Finished factory (the JF-691 config
variant folded into CreateFinishedHandlerOnClassManager's optional params), the
shared two-track scene helper for the two chain pins, and the rationale-prose
dedup (ONE home on the Finished veto helper; the chokepoint, DeviceQueue field,
and RecordEnqueue docs reduced to cross-references). SKIPPED with reasons: the
efficiency agent's two micro-notes (gating ResolvePlaybackOrder on
finishedIndex, one shared GetQueue snapshot) are unmeasurable at boundary
frequency and the second fights the shared static's signature; hoisting the
manager resolution in BuildAudioPlayerResponse would touch the pre-existing
RecordLastPlayed block (drive-by); a GetLastEnqueueSnapshot reader is ceremony
for a debug-only pair (the JF-626 precedent itself concedes non-atomic
writes); LastEnqueueNextItemId stays (debug policy asks item ids).

/code-review high (5 findings, all dispositioned same turn): (1) APPLIED, the
veto-ended boundary logged "queue exhausted" at Info with a mid-queue index;
it now has its own Info line and the pin demands it at Information level with
the exhausted line absent. (2) APPLIED, the four-term arm was a comment-pinned
copy of the resolver's admission guard; the guard now lives once as
ProgressReporter.ShuffleRandomPickApplies, consumed by ResolveNextItemId and
the Finished arm alike (the index-found precondition stays at the callers:
their "current item" resolutions legitimately differ). (3) DOCUMENTED in the
veto helper's doc, not coded: a displaced old stream's late Finished can now
end the session where the heuristic kept it alive, and the difference is inert
(the replacing play already carried ShouldEndSession=true; an enqueue-shaped
replacement records the displaced token as its after-token and passes the
veto); consulting the JF-655 displacement machinery here would serve the
active-audio flag's contract, not this one. (4) DECLINED, torn
(afterToken, nextItemId) debug pair on concurrent RecordEnqueue: the verdict
reads ONE atomic field; the worst case is a debug line naming a pair never
recorded together, below the JF-626 snapshot precedent's bar. (5) DECLINED,
PlayIntentHandler's two Enqueue sites omitting queueManager: production DI
makes the Plugin.Instance fallback the same singleton, so the ONE-writer
contract holds where it runs; no test drives that path hermetically today and
a ctor dependency would be speculative. Nothing was cut at a cap, so no
residual filing was needed.
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
