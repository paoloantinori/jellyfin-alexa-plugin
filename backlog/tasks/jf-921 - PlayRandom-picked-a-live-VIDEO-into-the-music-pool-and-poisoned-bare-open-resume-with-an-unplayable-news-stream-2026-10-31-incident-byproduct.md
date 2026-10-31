---
id: JF-921
title: >-
  PlayRandom picked a live VIDEO into the music pool and poisoned bare-open
  resume with an unplayable news stream (2026-10-31 incident byproduct)
status: To Do
assignee: []
created_date: '2026-10-31 16:37'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Discovered during the 2026-10-31 "songs broken" incident debug (root cause was the network VLAN cut, NOT the plugin; see JF-918/JF-920 context). Two product warts surfaced in the logs at 11:30:30:
1. PlayRandomIntent (it-IT, corr 4937108b) picked "100% NEWS (576p)" - a LIVE VIDEO item (zero duration) - into the random-MUSIC pool and launched it on a SCREENLESS Echo via the video-audio fallback. The /Audio/{videoId}/stream?static=true URL for that live source returns an HLS playlist (application/vnd.apple.mpegurl, 224 bytes), which an AudioPlayer device cannot play: silent playback failure.
2. Once that launch landed in the device's last-played ledger, EVERY subsequent bare open (LaunchRequest) auto-resumed the same broken live-video-as-audio, producing the observed retry loop (13:59-14:02, a LaunchRequest every ~20s, all replaying the news item, zero PlaybackStarted ever).
Fix directions: (a) the random-play pool (and any music-shaped play path) must exclude video/live items (zero-duration or Video kind) on audio-only launches, or at minimum skip items whose static-audio stream is not actually static audio; (b) the LaunchResume bare-open resume should not offer/replay a zero-duration live VIDEO item via the audio fallback (it is never playable that way on a screenless device). Priority low: the household incident was the network, but this wart will recur for any user with live/news video items in the library.
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
