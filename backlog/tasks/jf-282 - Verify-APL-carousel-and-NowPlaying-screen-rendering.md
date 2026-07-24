---
id: JF-282
title: Verify APL carousel and NowPlaying screen rendering
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-07-13 20:18'
labels:
  - e2e
  - apl
  - visual
milestone: m-5
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Apl/AplDirectiveBuilder.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
APL carousel templates render browse/search results as tappable image cards on Echo Show. Visual rendering has never been screenshot-verified and interactive taps are untested. Need to:
1. Trigger a browse/search that returns multiple results on Echo Show
2. Screenshot the carousel — verify album art, titles, and layout
3. Tap a carousel item — verify it triggers playback
4. Verify NowPlaying APL screen shows progress bar during playback
5. Verify graceful fallback on non-APL devices (no crash, audio-only response)
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
