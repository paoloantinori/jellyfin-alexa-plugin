---
id: JF-500
title: >-
  HEVC video sources still cannot play on the Echo Show (static kept with
  warning): add the video transcode tier to the episode HLS path
status: To Do
assignee: []
created_date: '2026-09-05 20:32'
labels:
  - tv
  - video
  - transcoding
  - echoshow
dependencies: []
references:
  - JF-498
  - corr=d9f848a7
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from JF-498's live verification (2026-09-05): the routing policy works and the remux path serves h264+eac3 sources, but HEVC-video sources (the entire Adolescence series - the one Paolo tested with - plus scattered HotD/Silo/The Bear/Rick and Morty/Bluey episodes) keep the static URL with a named-codec warning and cannot play on the Echo Show (H.264 only). The first cut deliberately excluded video transcoding. This task adds it: HEVC/AV1 video -> H.264 transcode in the episode HLS pipeline (video re-encode instead of copy; audio as today). Considerations: CPU cost on the minix hardware (HEVC decode + H.264 encode must sustain >= 1x realtime or playback catches up to the encode; measure first-segment time and sustained rate on the real box before promising anything; 4K HEVC sources may need scaling to 1080p to keep realtime - read the source resolution and decide, possibly behind a config flag for the transcode tier); quality preset (veryfast/ultrafast trade-off); the encode cache budget at transcode bitrates (the C1 playback pin already protects being-watched entries). Acceptance: an Adolescence episode plays end-to-end on the Echo Show with the video actually visible.
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
