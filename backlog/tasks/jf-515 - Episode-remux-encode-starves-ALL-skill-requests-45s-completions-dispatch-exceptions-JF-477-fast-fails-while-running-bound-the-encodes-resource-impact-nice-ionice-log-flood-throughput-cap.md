---
id: JF-515
title: >-
  Episode remux encode starves ALL skill requests (45s completions, dispatch
  exceptions, JF-477 fast-fails) while running: bound the encode's resource
  impact (nice/ionice, log flood, throughput cap)
status: Done
assignee: []
created_date: '2026-09-07 12:18'
updated_date: '2026-09-07 20:35'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REPRODUCTION + LEVER MEASUREMENTS (2026-09-07 evening, controlled runs on minix, PlayArtistSongs via Simulator as the probe, baseline idle 0.20-0.21s): R1 encode alone (Despicable Me 4, -c:v copy + AAC, ~65x realtime, 111% CPU=1 core of 4, load peak 1.8): 0.25-0.34s, NO starvation. R2 encode + device-like segment fetch via PUBLIC url + plugin Debug logging (box standing config: Serilog sync Console sink, only File is Async): spike class 4.31s reproduced (server-side gap INSIDE the Jellyfin items query, Completed=4304.7ms corr=02e83234), stderr flood measured 982 lines/60s. A second sporadic 4.44s spike at load 3.1-3.5 with two encodes. EXPERIMENT B, same encode+traffic with plugin override at Information (flood to console = 0): 10 queries, median 0.47s, MAX 1.39s, no 4s-class spike. EXPERIMENT P (priority): renice +19 + ionice idle on the live ffmpeg (succeeded as abc user after the first attempt hit the wrong PID): NO measurable improvement (0.03-1.96s with spikes persisting at load 3-4; the encode is not CPU-dominant). VERDICT: the stderr flood (16 lines/s at Debug on the sync console sink) is the measured lever; ffmpeg priority is RULED OUT by measurement (do not add nice/ionice without new evidence). 1-2s spike class persists even at Information at load 3+ (Jellyfin items query stalls under concurrent encode I/O + segment serving); untested candidate levers for that class, filed for follow-up: (a) ffmpeg source read via localhost instead of the public https URL (currently hairpins through Cloudflare+TLS; the plugin runs in-process so 127.0.0.1 is always reachable), (b) -readrate cap. FIX IMPLEMENTED: aggregating stderr drain in StartFfmpegProcess (errors immediate at Warning, routine lines counted with 30s Debug summaries + final summary). Box state during the work: plugin logging override temporarily at Information for experiments; RESTORE to Debug before closing.

GATES + MERGE + DEPLOY (2026-09-07 night): /simplify 4-agent run on the delta (applied: span-based classifier, redundant guard removed, counters simplified with corrected cumulative wording, docs deduplicated); code-review high returned FIX FIRST with two CONFIRMED findings, both applied (4 additional real-ffmpeg failure patterns verified against upstream sources: hlsenc 'Failed to open', mov 'moov atom', mp3 'Header missing', ENOSPC 'No space left on device'; fabricated test datum replaced with the real mov.c shape + 3 new cases; doubled <summary> tag deduped). Branch fix/jf515-stderr-flood (79eb870a), merged 63295722, suite 3449/3449 green on branch AND on main post-merge, build 0 warnings (analyzers on). DoD items 4-8 N/A (no session-attribute, HttpClient, model, E2E-visible behavior, or locale-string changes).

DEPLOYED to minix (AlexaSkill_0.12.1.0, md5 3d8fb2eb..., config survived users=1, Debug logging RESTORED from the experiment window in the same restart) and VERIFIED live: M1 playlist 200 + encode started; M2 with Debug ON: ZERO per-line 'ffmpeg stderr: [' lines during the encode and exactly one 30s summary ('622 routine lines suppressed so far'); M4 final line 'drained 4034 lines (4034 routine suppressed, 0 errors logged)' (old code would have written 4034 individual sync-console lines); M5 MediaInfo normal. M3 latency probes during this encode were contaminated (post-restart cold index + a TrueHD-source encode, the heaviest yet: 375s duration, ffmpeg 139% CPU) and are NOT counted as fix evidence; the causal evidence for the flood lever remains the controlled Information-vs-Debug A/B in the earlier notes.

NEW I/O CHARACTERIZATION for JF-517 (captured during the Raiders encode): box is I/O-bound at the spikes (iowait 21%, 36% us, 16% sy), ffmpeg 139% CPU on a TrueHD source, nginx 27% CPU (the encode source hairpins through the reverse proxy because -i uses the public https URL), 2.1GB in swap. The residual 6-19s stall class lives here, NOT in logging: JF-517(a) localhost source is now strongly indicated.

CLOSING: the task's own scope (bound the encode's resource impact) is resolved for the measured lever (log flood: implemented, verified); nice/ionice direction RULED OUT by measurement; throughput cap + localhost source handed to JF-517 with measurement protocol; pre-existing ExitCode-on-live-process defect filed as JF-518 from this change's review. The 45s incident itself: mechanism chain = flood (fixed) + I/O contention class (JF-517); live re-verification of the extreme case needs a real device session.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Measured, fixed, verified. Reproduced the 4.3-4.4s skill-request spike class on-device-harness; isolated the ffmpeg stderr per-line Debug flood on Serilog's synchronous console sink as the causal lever (controlled A/B: spikes with Debug, max 1.39s with Information); ruled out ffmpeg priority by direct measurement (renice+ionice: no effect). Fix: aggregating stderr drain in StartFfmpegProcess (real errors immediately at Warning with a classifier covering verified real-ffmpeg failure shapes; routine lines summarized every 30s + final total), span-based, 23 test cases. Gates: /simplify (findings applied), code-review high (FIX FIRST findings applied), 3449/3449 green, merged 63295722, deployed to minix and verified live (zero flood lines during a 4034-line encode, summaries working, config intact, Debug logging restored). Follow-ups: JF-517 (localhost encode source + readrate; new I/O evidence: iowait 21%, nginx hairpin 27%, TrueHD encode 139% CPU), JF-518 (pre-existing ExitCode-on-live-process from this review).
<!-- SECTION:FINAL_SUMMARY:END -->
