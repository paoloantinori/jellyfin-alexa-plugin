---
id: JF-539
title: >-
  Episode remux tier reads media streams twice: fold ResolveTotalMediaBitrateBps
  into ResolveSourceCodecs (one read; promote to record struct)
status: To Do
assignee: []
created_date: '2026-09-10 13:52'
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
