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
