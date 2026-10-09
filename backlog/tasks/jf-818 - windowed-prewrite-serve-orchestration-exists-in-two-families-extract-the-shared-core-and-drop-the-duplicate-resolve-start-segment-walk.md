---
id: JF-818
title: >-
  JF-818 - the windowed-prewrite ORCHESTRATION now exists in two families (episode + audiobook):
  extract the shared serve core, and drop the duplicate ResolveStartSegment walk
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - refactor
  - hls
  - tech-debt
dependencies:
  - JF-817
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-817 /simplify gate (three of four review angles converged on
it: reuse, simplification, altitude; the altitude pass judged it a follow-up,
not a defect, and that judgment is accepted).

JF-817 extracted the WINDOW MACHINERY (the growing-window computation and the
honor-band predicate, `ComputePrewriteWindow` +
`ResumeInsidePrewriteHonorBand`) into the ONE shared pair both serve families
consume, per its binding constraint. What it deliberately did NOT extract is
the ~30-line serve ORCHESTRATION around them, now duplicated line-for-line
between `TryServePrewrittenEpisodePlaylist` (episode) and
`ServeWindowedAudiobookPrewriteAsync` (audiobook):

read content once -> compute the window -> count segments -> windowed flag +
`TruncateToFirstSegments` -> resolve the resume segment -> honor-band drop
with an Information log -> windowed Debug log -> serve with the content
threaded as preloaded payload.

Only the serve terminator (`ServeEpisodePlaylistAsync` vs
`ServeAudiobookPlaylistAsync`, whose token/startTicks shapes genuinely
differ), the log nouns (item vs parent), and the nullable-vs-long startTicks
differ. The JF-637/JF-679 extract-at-the-second-copy rule has therefore fired
a second time on the sequence; a third prewrite family adopting windowing
(the song serve is the remaining unwindowed one, informational) would copy
the whole block again.

Two sub-items:

1. THE HOIST: one shared windowed-serve core next to `ComputePrewriteWindow`,
   parameterized by (segmentSeconds, floor, lead, log label, serve delegate).
   Behavior-identical including log text if the label is threaded; the honor
   band drop logs share the substring the capture pins match ("outside the
   encode window's honor band"). MIND the episode path's JF-679 ORDER
   CONTRACT comments (per-site log placement is pinned by the JF-677/JF-680
   log assertions) when moving the logs.
2. THE DUPLICATE WALK (efficiency angle, same gate): on a resume-during-encode
   fetch the start segment is resolved TWICE (the band check resolves it on
   the full listing, then `BuildResumePlaylist`/`BuildSlicedPlaylist`
   re-resolves it on the serve content; each is a full Split + EXTINF walk of
   a ~3000-entry listing, per playlist fetch, not per segment). The hoist is
   the natural place to thread the resolved segment through an optional
   parameter and drop one walk. Do both together or not at all; the walk
   alone does not justify widening `AudiobookPlaylistBuilder`'s API.
3. THE DEGRADE ARM (/code-review high finding 6, filed here because the
   shared core is the natural owner): the windowed serve delegates to
   `ServeResumePlaylistAsync`/`ServeAudiobookPlaylistAsync`, whose NON-vanish
   catch arms degrade to `PhysicalFile` over the RAW prewrite path, i.e. the
   full un-windowed listing, silently replacing the windowed bytes. Narrow
   (the preloaded-content path performs almost no IO inside the try), but the
   shared core should own a degrade that serves the already-windowed content
   instead of falling back to the death listing.

Skipped-as-judged at the same gate (do NOT reopen without new evidence): a
Task.WhenAll overlap of the content read and the window stat (one disk round
on a non-hot path, same shape as the episode twin), and folding the
segment-plant loop in the test fixtures (the other copies predate JF-817).
The song prewrite family's missing windowing is NOT part of this task: it is
a reachable audiobook defect of its own, filed as JF-819 (the JF-817 review
amended this file's earlier "informational" note about that serve).

AMENDED 2026-10-08 same-turn from the JF-819 /simplify gate (reuse +
simplification + altitude converged again, same finding as the JF-817 round;
the JF-819 worker skipped the hoist per this task's ownership, recording it
here instead):

- THE THIRD COPY LANDED, as this filing's own prediction stated it would:
  JF-819 windowed the song family (`TryServePrewrittenVideoAudioPlaylist`)
  riding the ONE shared pair, and the ~30-line ORCHESTRATION now exists
  VERBATIM in THREE families (episode ~1861, song ~2080, audiobook ~5293;
  differing only in family consts, segment seconds, log nouns/prefix, and
  the serve terminator). The hoist (sub-item 1) now covers three sites; the
  trigger the file's own EXTRACTION RECORD names has fired a third time.
  The JF-819 red-green pins sit on the ENDPOINT rows, so the hoist stays
  pin-covered on all three families.
- The test-fixture segment-plant skip above has its "new evidence": the
  third planting copy exists now too (`PlantLiveSingleItemEncodeCore`, the
  JF-819 fixture core, beside the episode `PlantLiveEncodeFixture` and
  audiobook `PlantLiveAudiobookEncodeFixture` bodies; axes: digit width D3
  vs D4, the writer delegate, arbitrary-cache-key without an item mock).
  Folding the three is IN SCOPE for this task's test half when it runs
  (low priority: arrange-only, no pin risk, but three copies of the planted
  stream.m3u8 shape).
- NOT drift, recorded so nobody "fixes" it: the song family's log prefix is
  deliberately `VideoAudio HLS` (the path's pre-existing prefix, including
  the JF-680-pinned "serving pre-written full listing" line), not
  `VideoAudio episode HLS`/`VideoAudio audiobook HLS`; per-family exact log
  wording is the controller's pinned convention.
- From the JF-819 /code-review high gate (2026-10-08, same-turn), two items
  that belong with this task's hoist rather than a local divergence:
  (a) the honor-band DROP LOG's outcome sentence: the JF-817 gate-marker F3
  wording ("joins at ITS live edge, roughly the encode-elapsed position, NOT
  at segment 0") is FALSE for floor-sized windows, where the live-edge join
  (end minus 3x TARGETDURATION, 12s) clamps past the 2-entry floor to the
  beginning; the JF-819 song copy already carries the regime-honest wording
  ("roughly the encode-elapsed position once the window has grown, and the
  beginning while it sits at the floor"); the EPISODE and AUDIOBOOK copies
  still carry the imprecise sentence and should take the corrected wording
  when the hoist unifies the logs (the capture pins match the "outside the
  encode window's honor band" substring, so the wording fix is pin-safe).
  (b) the per-poll allocation shape: every mid-encode playlist poll pays a
  full Split('\n') of the whole prewrite (a 5h single-file book is a
  ~4500-entry / ~250KB listing) inside TruncateToFirstSegments, plus up to
  three full EXTINF walks on an in-band resume, only to emit a window of a
  few dozen entries; the review's cheaper alternative (a span-based IndexOf
  line scan emitting only kept lines) belongs with the hoist, which already
  touches all three bodies and the builder API it would extend.
- From the JF-819 ORCHESTRATOR gate-marker (2026-10-08, same turn as the
  merge tail): the honor-band drop log fires at INFORMATION level on EVERY
  playlist poll that carries ?start= while outside the band. The player
  retains the ?start= query across event-playlist refreshes (the URL is
  unchanged), so a dropped-offset book resume re-logs the identical
  6-placeholder Information line once per refresh for the whole minutes-long
  encode window, dozens of near-identical lines per incident in the
  Information-default log the debug-logging policy keeps clean for triage.
  The hoist owns all three log bodies: log the drop ONCE per generation
  (or Debug after the first occurrence), keeping Information for the first
  line triage actually needs.

LANDED 2026-10-09 (all four file items): the ONE shared core
`ServeWindowedPrewriteAsync` now owns the orchestration next to
`ComputePrewriteWindow`/`ResumeInsidePrewriteHonorBand`, parameterized by the
`WindowedPrewriteServeFamily` record (segmentSeconds/floor/lead + the
family's log label/noun/drop attribution/windowed rationale/serving-line
template); episode and song keep only their prewrite probe plus the family
record, the audiobook body collapsed to a one-line wrapper over the core.
Sub-item 2: the in-band start segment threads from the honor-band walk
through `ServePlaylistSlicedAsync(preloadedStartSegment)` into
`BuildResumePlaylist`/`BuildSlicedPlaylist(resolvedStartSegment)`; the
serve's second full EXTINF walk per playlist fetch is gone (the Debug
segment log recompute went with it). Sub-item 3: the core owns the
non-vanish degrade and serves the ALREADY-WINDOWED content
(`ServeTokenizedPlaylistContent`, the token tail extracted to the one
helper it now shares with `ServePlaylistWithTokenAsync` and
`ServePlaylistSlicedAsync`); the audiobook prewrite rows left
`ServeAudiobookPlaylistAsync` (whose catch degraded to PhysicalFile over
the raw death listing) for `ServePlaylistSlicedAsync`, happy-path bytes and
vanish propagation proven identical, warm ENDLIST rows untouched. Item (a):
the episode and audiobook drop logs took the JF-819 regime-honest outcome
sentence; the rendered per-family text is otherwise byte-identical (the
structured log PROPERTY names are unified to {LogLabel}/{EntityNoun}/
{EntityId}/..., recorded on the family record's doc). Item (b):
`TruncateToFirstSegments` is now a span-based IndexOf line scan emitting
only kept lines (zero per-line strings, breaks at the truncation point;
`IsSegmentUriLine` gained the span twin so ONE predicate body serves both
entry points; Split/Join byte-equality is pinned by the builder's exact-
equality test). Item (c): the drop log latches once per prewrite
generation (Information first, Debug after; keyed by the prewrite path,
`HonorBandDropLoggedPrewrites`). Tests: the three planters folded into
`PlantLivePrewriteEncodeCore` (+ `PlantedLivePlaylistShape`), one new latch
pin; baseline 335 = post-hoist 335 with ZERO pin edits, final full suite
5569/5569 both TFMs.

FOLLOW-UPS FILED FROM THE /simplify ROUND (this file is the tracker):
1. THE LATCH RESET (the altitude angle, dispositioned skip-with-filing:
   touching `ActiveEncodeGenerations` is outside the hoist diff): the path
   key has two flaws a registry-threaded flag would close: (i) a re-encode
   at UNCHANGED art ticks recreates the same prewrite path, so that fresh
   encode's first out-of-band drop logs at Debug (the doc on the latch
   records the limit honestly); (ii) entries are never evicted (one short
   string per dropped-generation path, process lifetime). The deeper fix
   converges with the anchor follow-up already recorded on
   `TryServePrewrittenEpisodePlaylist`'s doc: thread the real encode-start
   anchor (and this latch's flag) through the encode registries, whose
   generation entries already evict at encode end.
2. THE EPISODE PLANTER'S LIVE PARTIAL (the same round's own F2 shape, kept
   deliberately during the fold): the consolidated
   `PlantLivePrewriteEncodeCore` preserves the episode planter's historical
   FixedTwoEntries stream.m3u8 (2 entries beside headSegmentCount segment
   files). The single-item planter moved OFF that disagreeing shape at
   JF-819 (the F2 rationale); the episode twin still carries it, so a
   future pin built on that planter that touches the JF-503 hold path will
   fail on the fixture. Switching it to AllPlantedSegments (and deleting
   the enum case) is a one-line follow-up whose net is a suite run.
3. THE RESUME-POLL SPLIT RESIDUAL (the /code-review high gate's
   allocation finding, half-rejected on the facts): after item (b) the
   only remaining full-content allocation on a mid-encode poll is the
   honor-band walk's `TryResolveStartSegmentByExtinf` Split, and only on
   RESUME polls (startTicks > 0); the walk itself early-exits at the
   in-band start segment, and `CountSegmentsInPlaylist` was already
   allocation-free (an IndexOf loop; the reviewer's Split claim about it
   is wrong). Converting the EXTINF walk to the same span-scan shape is a
   builder-side rewrite touching every slice path, not just the prewrite
   family: filed here rather than folded into the hoist.
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
