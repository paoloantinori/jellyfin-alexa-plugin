---
id: JF-925
title: >-
  Feature exploration (parked): burned-in synced lyrics on the Echo Show via the
  video-audio player; four gating prerequisites
status: To Do
assignee: []
created_date: '2026-11-08 17:10'
updated_date: '2026-11-09 06:00'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Feature exploration (maintainer-approved filing 2026-11-08, parked by design): lyrics support for songs on the Echo Show. Full assessment probe-verified on minix 2026-11-08 (Jellyfin 12.4, lscr.io image):

WHAT EXISTS:
- Jellyfin 12.4 serves lyrics natively: GET /Audio/{itemId}/Lyrics (verified working, returns LyricsDto with per-line Text). Coverage in library: ~223/1000 first-page audio items have HasLyrics=true (~22% of 12.7k total).
- The served texts are PLAIN/UNSYNCED (zero StartPositionTicks in sampled items): scrolling synced lyrics need lrc files alongside media, which the library does not have today.

THE WALL (device-proven, JF-624): on the AudioPlayer path (all current music playback), the Echo Show's native now-playing screen COVERS any skill APL document during playback, and never shows lyrics for custom-skill streams. On screenless Echos there is no screen at all. So no lyrics route exists on the current playback path, by platform construction.

THE ONLY VISIBLE ROUTE: the video-audio player (issue #40, the parked seek-mode path): the plugin OWNS the ffmpeg command, so synced lyrics could be rendered into the video (drawtext per-line with enable windows, or .ass subtitles): burned-in scrolling lyrics on the Show during playback.

FOUR GATING PREREQUISITES (any one unlocking re-opens this):
(1) CONTAINER FFMPEG: the lscr.io image's ffmpeg has NEITHER drawtext NOR subtitles (verified: no freetype/ass in the build). Needs a replaced/mounted fuller ffmpeg binary (verify linuxserver image override support) or an image change.
(2) SYNCED LYRICS: library texts are unsynced txt. Need lrc files (scrapers/lrc providers or user-supplied) for scrolling sync; naive whole-text display is the poor fallback.
(3) UN-PARK OR SCOPE: the video-audio music path is parked by maintainer decision (issue #40, transport/encode trade-offs). Lyrics would ride that infrastructure: either un-park it or design a lyrics-only opt-in variant accepting the same trade-offs.
(4) HARDWARE SCOPE: Show-class devices only (screenless Echos excluded by construction; the reporter's Sonos is AudioPlayer-only, also excluded).

DESIGN SKETCH AT GATE-CLEAR: opt-in flag (config), lyrics lookup via /Audio/{id}/Lyrics at transcode time, lrc ticks mapped onto the transcode timeline (mind offset/remux adjustments as in the resume-slicing pipeline), drawtext filtergraph (one filter per line, enable=between(t,start,end)) or .ass if libass arrives; fallback to the current no-lyrics stream when no lyrics/sync data. Voice flow idea: 'lyrics' query on a playing song could relaunch via VideoApp with lyrics (needs the relaunch trade-offs assessed vs the JF-624 covering rules for the AudioPlayer it would interrupt).

Verify prerequisites (esp. 1) before any design work; each is probe-checked with commands recorded in this task's history (session 2026-11-08).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-11-09 SPIKE COMPLETE (worktree jf925-lyrics-spike, commit on that branch; scripts + report in claudedocs/spike_jf-925_lyrics_2026-11-09.md): the two hardest prerequisites are now PROVEN or dissolved. (1) ffmpeg: static johnvansickle build (libass, subtitles filter) works on the minix HOST: 1-2s encode per song, 8-10MB/track, exact 10s-segment HLS shape; remaining integration = mounting the binary into the lscr.io container (minix_ansible_config decision), NOT an image fork. (2) synced lyrics: the library ALREADY has real .lrc sidecars (Goshen 24/24 and Vagabond 33/33 lines served with Start ticks; 12.4 DTO field is Start, not StartPositionTicks; Santa Fe oddity: lrc on disk but serves 0 lines, unfollowed). Plugin can also WRITE lyrics: POST /Audio/{id}/Lyrics?fileName=x.lrc raw text body (fileName is a QUERY param per v12.4 source) = 200; DELETE = 204; both exercised and reverted on The Peacock. (4) burn-in sync VISUALLY verified: frames at known timestamps show exactly the right line, on synthetic AND organic lrc. REVISED GATES: only issue #40 (un-park video-audio music) + the container ffmpeg mount remain. Spike artifacts kept on minix ~/lyrics-spike (~40MB) for the future feature.
<!-- SECTION:NOTES:END -->
