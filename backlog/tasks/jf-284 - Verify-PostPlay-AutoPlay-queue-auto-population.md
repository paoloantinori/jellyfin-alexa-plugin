---
id: JF-284
title: Verify PostPlay AutoPlay queue auto-population
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-07-13 20:17'
labels:
  - e2e
  - playback
  - autoplay
milestone: m-5
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PostPlay AutoPlay (PlaybackNearlyFinished + FindRadioTracksAsync) enqueues similar tracks when queue exhausts. Intertwined with Radio mode. Never tested live. Need to:
1. Enable AutoPlay, play a single song, wait for it to finish
2. Verify next track auto-enqueues before current track ends (gapless)
3. Verify AutoPlay continues beyond first auto-enqueued track
4. Test interaction: AutoPlay on + Radio on — which wins?
5. Test AutoPlay on + shuffle on — does it shuffle the auto-populated tracks?
6. Verify "stop" during AutoPlay actually stops (no runaway queue)
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
