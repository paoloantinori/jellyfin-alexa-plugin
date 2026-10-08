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
