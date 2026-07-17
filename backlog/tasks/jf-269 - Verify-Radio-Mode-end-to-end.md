---
id: JF-269
title: Verify Radio Mode end-to-end
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:16'
labels:
  - e2e
  - playback
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/TurnRadioOnIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/TurnRadioOffIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
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
