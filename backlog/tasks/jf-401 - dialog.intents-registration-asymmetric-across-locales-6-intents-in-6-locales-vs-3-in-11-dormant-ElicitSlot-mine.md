---
id: JF-401
title: >-
  dialog.intents registration asymmetric across locales (6 intents in 6 locales
  vs 3 in 11) - dormant ElicitSlot mine
status: Done
assignee: []
created_date: '2026-08-23 05:57'
updated_date: '2026-08-23 06:26'
labels:
  - interaction-model
  - localization
  - tech-debt
milestone: m-17
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Model hygiene finding (2026-08-23 audit). dialog.intents registration is not uniform: en-US, pt-BR, nl-NL, ja-JP, hi-IN, ar-SA register 6 intents (PlayEpisode, PlaySong, PlayAlbum, FindSong, FindSongByArtist, ShufflePlay); the other 11 locales register only 3 (FindSong, FindSongByArtist, ShufflePlay). Today only FindSong elicits slots so nothing breaks, but if code ever elicits a slot for PlaySong/PlayAlbum/PlayEpisode in the 11-locale group, the Dialog.ElicitSlot directive is SILENTLY ignored (anti-pattern #9, the class of bug that produced the 2026-08-21 live incident). Align all 17 locales to the same dialog.intents set (mechanical model change; it-IT via the YAML template).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in 3b314a1. All 17 locales now register the same 6 dialog.intents (PlayEpisode/PlaySong/PlayAlbum added to the 11 that had only 3; entries built from each locale's own slot shapes; it-IT via the YAML template + regeneration, idempotent). validate_interaction_models PASS. Models not yet pushed to Amazon: next /deploy or rebuild models will carry them.
<!-- SECTION:FINAL_SUMMARY:END -->
