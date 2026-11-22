---
id: JF-945
title: >-
  Two it-IT wrapper battery rows are trainer-marginal (SleepTimer + PlayPodcast
  one-shot wrappers flip across identical-content rebuilds)
status: To Do
assignee: []
created_date: '2026-11-22 17:07'
updated_date: '2026-11-22 17:07'
labels:
  - nlu
  - battery
  - flaky
dependencies: []
references:
  - scripts/nlu_wrapper_battery.py
  - scripts/nlu-lab/runs/20261122-172731-stock-it-bare.json
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two pre-existing it-IT wrapper battery rows are trainer-marginal and flip across runs with NO model change on their intents: 'chiedi a mia collezione di fermare dopo un minuto' (expected SleepTimerIntent) and 'chiedi a mia collezione di riprodurre il podcast generazione' (expected PlayPodcastIntent). Evidence 2026-11-22: live JF-940 model flips between PlayRadio/AddSongToPlaylist/occasionally-correct across three probes minutes apart; the LAB skill with the STOCK model hard-fails BOTH to AMAZON.FallbackIntent (run 20261122-172731-stock-it-bare, bare tails route correctly: 'riprodurre il podcast generazione' -> PlayPodcastIntent); sleep-timer selected correctly on one re-run then misrouted on the next. The JF-940 it-IT template delta touches only PlaySong/PlayLibrarySong/SongTitle (verified by template diff), so these rows are unrelated regressions; per the NLU trainer nondeterminism memory any full-model rebuild can flip marginal phrases even with identical content on their intents. Options to evaluate: strengthen the wrapper tails (add more anchoring samples for SleepTimer/PlayPodcast infinitive forms), mark the rows with a flaky-tolerant expectation, or split them out of the blocking battery. Until then the battery intermittently reads 19/21 or 20/21 with these two FAILs.
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
