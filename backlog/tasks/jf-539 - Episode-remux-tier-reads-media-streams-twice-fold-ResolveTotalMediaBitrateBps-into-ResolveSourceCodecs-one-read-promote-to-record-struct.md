---
id: JF-539
title: >-
  Episode remux tier reads media streams twice: fold ResolveTotalMediaBitrateBps
  into ResolveSourceCodecs (one read; promote to record struct)
status: Done
assignee: []
created_date: '2026-09-10 13:52'
updated_date: '2026-09-10 18:14'
labels:
  - tech-debt
  - video-audio
  - efficiency
dependencies: []
references:
  - JF-525
  - JF-500
  - VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-525 /simplify pass (2026-09-10): the episode REMUX tier still reads the item's media stream list TWICE - ResolveSourceCodecs at StreamHlsEpisodeCore:~662 (JF-525 folded codecs to one read) then ResolveTotalMediaBitrateBps at :~765, which re-runs TryGetMediaStreams. The transcode tier is already at one read (pinned by StreamHlsEpisode_TranscodeTier_ReadsMediaStreamsOnce, Times.Once). Folding the bitrate probe into ResolveSourceCodecs would take the remux tier to one read too; at that point the return shape should be promoted from the named 2-tuple to a readonly record struct (repo precedent: HandlerSelection, AudioLaunchSource - the JF-525 simplify gate's stated promotion trigger). Note the song paths (StreamHlsVideoAudioCore :210/:420) read only the audio codec once each and are NOT part of this. Small, mechanical, guard the fail-open shapes.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge e72c54c7 + the /simplify finding commit c3203d7a. Both episode tiers now read the media stream list ONCE: ResolveSourceCodecs returns internal readonly record struct SourceMediaProbe(Video, Audio, TotalBitrateBps) computed in a single pass (codec sides byte-identical; bitrate side folded expression-for-expression from the deleted single-caller ResolveTotalMediaBitrateBps - first-per-type ??=, blank/cover streams still contribute BitRate, int-sum + >0 normalization, fail-open default); the episode path destructures one probe for tier decision + args + remux estimate; the 2 unit tests renamed with unchanged fixtures; remux-tier Times.Once test mechanically red-proven pre-fold. Gates: combined simplify+code-review micro-diff gate (all 5 checks verified against git show HEAD, including the bitrate-sum semantics the brief challenged); the explicit /simplify pass (its finding applied in c3203d7a: the stale TryGetMediaStreams plural; plus the pre-existing ExtractCodecs doc overstatement fixed in the same commit); suites 3579/3579 net9.0 independent, 0 warnings both TFMs. Informational notes recorded, not acted: ResolveSourceCodecs' name narrower than its return (deliberate scope; rename later bundled with real work); the pre-existing int-sum wrap quirk unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
