---
id: JF-499
title: >-
  Episode HLS lifecycle watch items: restart vs _episodeHlsItems, 30-min monitor
  timeout, fast-path FileNotFoundException race, Cleanup missing
  UnauthorizedAccessException
status: To Do
assignee: []
created_date: '2026-09-05 20:05'
labels:
  - video
  - hls
  - cache
dependencies: []
references:
  - JF-498
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Below-bar watch items from the JF-498 formal review (2026-09-05, reviewer-invoked tracking rule), all in the new episode HLS endpoint's lifecycle. W1: _episodeHlsItems is process-static, so after a Jellyfin restart cached-episode segment fetches resume growing the audiobook position-tracking file (the GetSegment skip only works within the encoding process's lifetime; benign but the stated rationale only half-holds). W2: MonitorFfmpegHlsAsync's 30-minute timeout kills a slow (NAS-bound) episode remux mid-encode, producing debris mid-playback, and the self-heal re-encode restarts the timeline from 0:00 (this path has no resume slice). W3: fast-path race: a request holding a pre-Cleanup FileInfo while a lock-holder invalidates debris can throw FileNotFoundException from ServePlaylistWithToken: one 500 playlist fetch, self-heals on the Echo's retry. W4: VideoAudioCache.Cleanup catches IOException but not UnauthorizedAccessException; on a permission-denied dir the exception propagates out of the playlist fast path as a 500. None blocks the JF-498 deploy; fix opportunistically (W2 and W3 are the most user-visible).
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
