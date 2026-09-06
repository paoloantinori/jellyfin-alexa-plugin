---
id: JF-503
title: >-
  Seek on the episode HLS errors out when it lands beyond the running encode's
  head: hold-for-segment for near-ahead seeks + document the boundary + log
  segment 404s
status: To Do
assignee: []
created_date: '2026-09-06 08:48'
labels:
  - video
  - hls
  - seek
dependencies: []
references:
  - JF-498
  - device test 2026-09-06
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 device test (item 4 of the verification card): seeking on the episode HLS during playback produced 'various errors' and killed playback, while the same content plays fine start-to-end. Prime mechanism: seek-AHEAD-of-the-encode - the remux runs at ~20x realtime (Silo's 777 segments completed ~3min after start), so a seek beyond the encoded head during the first minutes requests a segment that does not exist yet in the event playlist; ExoPlayer errors hard on the missing segment instead of waiting. A seek WITHIN the encoded head or after completion should work (untested on device; the playlist has ENDLIST only at completion). Options: (a) accept and document the boundary (seek reliable after ~2-3min or within the encoded head); (b) hold-for-segment: when a GetSegment request names the NEXT expected segment of an active encode, block up to ~3-4s until ffmpeg writes it (encode is 20x realtime so the segment is imminent), returning it instead of a 404 - smooths seeks to just-beyond-head; (c) serve a redirect-to-latest for far-ahead seeks (fragile, probably wrong). Recommended: (b) for the near-ahead case + document the far-ahead boundary. Also log GetSegment 404s at debug with the requested name and the highest existing segment, so the next device session can confirm the mechanism from the logs (this session could not: GetSegment does not log).
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
