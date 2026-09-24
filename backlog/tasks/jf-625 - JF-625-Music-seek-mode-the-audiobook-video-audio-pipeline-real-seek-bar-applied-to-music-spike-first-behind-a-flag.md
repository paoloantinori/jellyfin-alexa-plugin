---
id: JF-625
title: >-
  JF-625 - Music seek mode: the audiobook video-audio pipeline (real seek bar)
  applied to music, spike-first behind a flag
status: To Do
assignee: []
created_date: '2026-09-24 16:16'
updated_date: '2026-09-24 16:16'
labels: []
milestone: 1.0 polish
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Born from the JF-624 round-8 verdict exchange (2026-09-24): Paolo challenged the 'loses queue/radio/gapless/cover' dismissal and the challenge was CORRECT - those were unbuilt features, not intrinsic losses. The one intrinsic constraint is that VideoApp emits no playback events to the skill (position tracking via segment fetches already exists: AudiobookPositionTracker). Goal: music playback with a REAL seek bar on the Echo Show via the existing audiobook video-audio HLS pipeline (1fps stillimage video + audio copy), instead of the AudioPlayer path where the native surface has no scrubber for custom skills (MSAPI-only) and covers any APL document.

Plan (spike-first, behind a new flag default OFF, e.g. MusicSeekMode):
1. SPIKE: single song via video-audio with COVER ART as the video track (-loop 1 -i cover.jpg + scale/pad to 1280x720, -tune stillimage, -g 1, 10s segments) instead of the black frame; verify on the live Show: playback, cover visible, seek bar present and functional (seek across the track), no black screen.
2. Queue-as-concat: album/playlist = one concat stream (the audiobook chapter shape with tracks), per-track offsets known server-side for MediaInfo/resume.
3. Radio extension: event playlist without ENDLIST; server appends next radio track's segments before exhaustion (AutoPopulate logic moves server-side).
4. Resume via the existing segment-based position tracking + sliced playlist (?start=).
5. Keep AudioPlayer as the default path; the mode is per-user opt-in (config flag), so nothing in the critical path changes.

Known costs to state in the config UI: a few seconds of first-play encode latency; voice transport during playback unchanged (no worse than today). Track title/artist can be baked into the video frame later (polish).
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



## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 [ ] SPIKE gate: one song plays via video-audio with the album cover as the video track on the live Show: audio correct, cover visible, seek bar PRESENT and a mid-track seek lands correctly, no black screen
- [ ] #2 [ ] Flag MusicSeekMode (default OFF) gates the whole path; flag off = byte-identical AudioPlayer behavior
- [ ] #3 [ ] Position tracking works via segment fetches (AudiobookPositionTracker shape) and MediaInfo answers the right track/position
- [ ] #4 [ ] Queue-as-concat: an album plays start to finish as one stream with correct per-track reporting
- [ ] #5 [ ] Radio extension: the event playlist appends segments before exhaustion and playback continues seamlessly
- [ ] #6 [ ] First-play latency measured and documented (encode start to audio out)
<!-- AC:END -->
