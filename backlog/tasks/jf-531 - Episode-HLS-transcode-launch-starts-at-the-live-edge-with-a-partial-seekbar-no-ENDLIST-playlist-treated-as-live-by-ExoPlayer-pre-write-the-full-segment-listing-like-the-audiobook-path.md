---
id: JF-531
title: >-
  Episode HLS transcode launch starts at the live edge with a partial seekbar
  (no-ENDLIST playlist treated as live by ExoPlayer); pre-write the full segment
  listing like the audiobook path
status: To Do
assignee: []
created_date: '2026-09-09 16:29'
labels:
  - bug
  - video
  - hls
  - transcoding
  - echoshow
  - device-evidence
dependencies: []
references:
  - corr=c0c21c6a
  - JF-500
  - JF-498
  - JF-292
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's live Echo Show session (2026-09-09 18:10, corr=c0c21c6a): launching Adolescence E2 (51 min, HEVC -> transcode tier) via VideoApp produced a 6-MINUTE seekbar with playback starting near the end. VERIFIED FACTS: the launch URL has NO ?start= (no resume slicing; the initial resume-slice explanation was wrong and retracted); the playlist is an event playlist (-hls_flags append_list, no ENDLIST) that grows as the encode proceeds at ~4.4x. MECHANISM: ExoPlayer treats a no-ENDLIST playlist as LIVE - it starts at the live edge (the newest available segment) and the seekbar shows the encoded-so-far window, not the true runtime. So every transcode-tier episode launch starts mid-episode and shows a growing partial bar for the first ~runtime/4.4 minutes. The remux tier (~21x) has the same shape but reaches full length ~5x faster, masking the issue. FIX DIRECTION (proven in-codebase): the audiobook path pre-writes the full playlist listing at first serve (files appear progressively; the CLAUDE.md HLS notes document this as 'how first-play gets correct total duration'). Apply the same pre-written listing to the episode path. Secondary observation same session: the E2 cache dir is absent ~10 min post-play (0 segments) - either the 2.6GB reserve vs the 2048MB default cap evicted it or cleanup removed an in-progress encode; check whether the transcode tier should raise the default VideoAudioCacheSizeMB (the JF-500 E3 doc note anticipated this).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Pre-write the full segment list in the episode HLS playlist (audiobook pattern: all segment names listed at first serve, files appearing progressively) for BOTH the remux and transcode tiers, or document why remux is exempt
- [ ] #2 On-device verified: an HEVC episode launch shows the true runtime on the seekbar and playback starts at 0 (not the live edge); Paolo's Adolescence E2 case is the reference (51-min episode showed a 6-min bar and started near the end)
- [ ] #3 The audiobook path's behavior is unchanged (it already pre-writes); regression tests cover the episode playlist shape (full listing at first serve, no ENDLIST until complete, append semantics preserved for mid-encode fetches)
- [ ] #4 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

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
