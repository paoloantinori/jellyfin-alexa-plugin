---
id: JF-515
title: >-
  Episode remux encode starves ALL skill requests (45s completions, dispatch
  exceptions, JF-477 fast-fails) while running: bound the encode's resource
  impact (nice/ionice, log flood, throughput cap)
status: To Do
assignee: []
created_date: '2026-09-07 12:18'
labels:
  - performance
  - video
  - hls
  - resource-contention
dependencies: []
references:
  - corr=32f8bb92
  - corr=8d4c5b6e
  - corr=6b33a759
  - corr=0f4da373
  - JF-498
  - JF-477
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-07 14:11-14:17 device session: while the Inside Out 2 episode HLS remux was encoding (started by the successful test-2 play, corr=32f8bb92), EVERY subsequent skill request on BOTH devices degraded or died: (a) a PlayArtistSongs request took 45 SECONDS to complete (corr=8d4c5b6e, entered 14:12:19, completed 14:13:04); (b) two requests died with SessionEnded InvalidResponse 'An exception occurred while dispatching the request to the skill' (14:12:30 corr=6b33a759 request b892ccee, and 14:14:01/14:14:17 on the Dot) - the 14:12:30 request NEVER reached the controller (no Processing line), i.e. the server was unresponsive enough that Amazon's dispatch failed; (c) the Dot's first PlayNextEpisode hit the JF-477 2s fast-fail ('Utente non trovato', corr=0f4da373); (d) even 'apri mia collezione' on the Dot failed audibly (the LaunchRequest DID arrive at 14:14:05 and we answered with the resume offer, but apparently outside the usable window). Box state during the window: load average 5.5+ (still 5.48 at 14:17), ONE ffmpeg at ~49x (audio copy + AAC, mostly I/O: reading the source over HTTPS and writing segments), and the podman log carried 3866 ffmpeg-stderr Debug lines in 40 minutes (the per-segment 'Opening ... for writing' flood from the JF-498 remux, ~2 lines per 4s segment).

This is the resource-contention bill of JF-498 landing: the encode gate (MaxConcurrentFfmpegEncodes=2) bounds CONCURRENT ffmpeg processes but NOTHING bounds the encode's impact on request latency - not CPU priority (the ffmpeg runs at normal nice), not I/O, and not the Debug-log flood volume. It also retroactively explains the 2026-09-06 'continua a guardare' 6-28s NextUp spikes (the Silo encode was running then; my controlled re-run measured 91-131ms with ONE encode at IDLE - the difference is concurrent playback traffic + session lookups + the log flood).

FIX DIRECTIONS (pick with measurement): (1) lower the ffmpeg process priority (nice/ionice on the encode spawn: StartFfmpegProcessGatedAsync) - cheapest, likely the biggest lever since the box is I/O-bound; (2) the stderr reader's per-segment Debug lines: demote the 'Opening X for writing' lines to Trace or sample them (3866 lines/40min is log noise but also serializer+pipe work per segment); (3) consider capping the encode throughput (ffmpeg -readrate or a rate limiter) so a 49x encode does not monopolize the disk when the source read is the bottleneck; (4) the JF-477 session-lookup budget may need headroom awareness (skip the DB roundtrip when a GetSessionByAuthenticationToken was recently slow). Reproduce first: start an episode encode, then fire PlayArtistSongs/NextUp requests and measure (the 2026-09-06 probe was at idle and missed this).
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
