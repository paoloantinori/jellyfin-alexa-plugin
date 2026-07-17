---
id: JF-184
title: >-
  Research: APL progress bar and elapsed/total time display for audio, video,
  and books
status: Done
assignee: []
created_date: '2026-05-19 16:44'
updated_date: '2026-05-19 18:34'
labels:
  - research
  - apl
  - ux
  - playback
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Investigate whether the Jellyfin Alexa skill can display a progress bar and elapsed/total time information on APL-capable devices (Echo Show) for audio, video, and audiobook playback.

Current state:
- The NowPlaying APL template shows album art, title, and playback controls (prev/pause/next)
- It does NOT show elapsed time, total duration, or a visual progress bar
- AudioPlayer directives carry offset information but the APL screen doesn't reflect playback position

Research questions:
1. Can APL templates access AudioPlayer playback position in real-time? (e.g., via data binding or tick handlers)
2. Can the skill send APL ExecuteCommands to update a progress bar periodically during playback?
3. What APL components support progress bars? (Slider, ProgressBar, or custom Container-based)
4. Does Jellyfin provide duration metadata that can be passed to the APL template?
5. What are the Alexa best practices for displaying playback progress on Echo Show?
6. Are there timer/interval mechanisms in APL for live position updates?
7. How do other Alexa skills (Spotify, Amazon Music) show progress on Echo Show?

Scope:
- Audio playback (music, audiobooks)
- Video playback (if applicable to Echo Show)
- Books (audiobooks with chapter/position info)
- All supported APL devices
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Research complete. Feasible: AlexaProgressBar with handleTick for client-side self-advancing bar, elapsed/total time text. NOT feasible: native scrubber (Music API only), real-time AudioPlayer sync, seek/scrub. Approach: init with offset, handleTick +1000ms/s, Text for mm:ss, drift ~1-3s acceptable.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Research complete. Findings: AlexaProgressBar + handleTick is the documented approach for custom skills. No third-party examples exist but all building blocks are officially supported. Native scrubber (Spotify style) is Music API exclusive. Implementation task created as JF-186.
<!-- SECTION:FINAL_SUMMARY:END -->
