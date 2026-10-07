---
id: JF-282
title: Verify APL carousel and NowPlaying screen rendering
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-10-07 08:43'
labels:
  - e2e
  - apl
  - visual
milestone: m-18
dependencies: []
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

AUDIT UPDATE (2026-10-02): item 4 is DEAD as stated - the JF-624 live card-drop experiment proved the native now-playing surface covers skill APL during AudioPlayer playback, so the enhanced music NowPlaying goal is unattainable (see CLAUDE.md Key Gotchas). Items 1-3 and 5 remain real device work; the tap path exists (Alexa/Apl/AplUserEventHandler.cs). Apply the task's own 2026-09-27 rewrite prescription when picking this up.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 audit correction: item 4 ('NowPlaying APL screen shows progress bar during playback') is DEAD AS STATED - JF-624 proved the Echo's native full-screen player covers any skill APL during AudioPlayer playback and a live progress bar is unattainable for custom skills (CLAUDE.md Key Gotchas, the JF-624 entry). Rewrite item 4 to the attainable claim before the device session: the launch-moment card flash and the non-playback surfaces (browse lists, disambiguation).
<!-- SECTION:NOTES:END -->
