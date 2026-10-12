---
id: JF-872
title: >-
  JF-873: seek-mode music stop speaks the video line ("ho fermato il video")
  instead of a music line
status: To Do
assignee: []
created_date: '2026-10-12 16:49'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-870 round (worker's code-review finding, deliberately out of that change's scope): when seek-mode music is active (NativeControlsForAudio/VideoAppForAudio ON, the JF-625 machinery that stays shipped but OFF by default), stopping playback by voice speaks VideoStoppedByVoice ("ho fermato il video") because PauseIntentHandler's medium-aware line only distinguishes the AUDIOBOOK branch so far. PlayingMedium.VideoAppAudio should get its own natural line (e.g. it-IT "Ho fermato la musica.") in the same BookStoppedByVoice pattern: key in all 17 locales, branch in the VideoApp-family arm, ledger row, test pin. Low priority: the path is OFF by default after the JF-625 maintainer NO (2026-10-11), so only opt-in users hear the wrong line. Live TV keeps the video line (canonical for a channel stream).
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
