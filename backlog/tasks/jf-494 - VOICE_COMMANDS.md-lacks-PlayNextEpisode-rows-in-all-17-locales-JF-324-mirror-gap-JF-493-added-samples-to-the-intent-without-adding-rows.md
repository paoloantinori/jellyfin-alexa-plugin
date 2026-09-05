---
id: JF-494
title: >-
  VOICE_COMMANDS.md lacks PlayNextEpisode rows in all 17 locales (JF-324 mirror
  gap; JF-493 added samples to the intent without adding rows)
status: To Do
assignee: []
created_date: '2026-09-05 12:40'
labels:
  - documentation
  - tv
  - nlu
dependencies: []
references:
  - JF-324
  - JF-493
  - VOICE_COMMANDS.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-324 added PlayNextEpisodeIntent to all 17 locale models but never added the intent's rows to VOICE_COMMANDS.md (the hand-maintained per-locale utterance table). JF-493 (2026-09-05) added infinitive one-shot samples to the intent in 9 locales and updated only the Play Episode rows of the mirror, leaving the PlayNextEpisode gap unchanged (pre-existing, out of that diff's scope). The table therefore documents no way to ask for the next/last episode in any locale even though every model carries the intent. Add a "Play Next Episode" row per locale section (17 rows) mirroring each model's current PlayNextEpisodeIntent samples, including the JF-493 infinitive twins (it-IT "{infinitive} il prossimo/l'ultimo episodio di {series_name}", "{infinitive} la serie {series_name}"; en "to play the next episode..."; de "Zu spielen die nächste folge..."; fr "De jouer le prochain épisode..."). Pure documentation change, no code or model edits.
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
