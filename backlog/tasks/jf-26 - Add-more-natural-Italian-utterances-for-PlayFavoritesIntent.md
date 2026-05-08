---
id: JF-26
title: Add more natural Italian utterances for PlayFavoritesIntent
status: Done
assignee: []
created_date: '2026-05-03 06:34'
updated_date: '2026-05-03 06:56'
labels:
  - enhancement
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Italian PlayFavoritesIntent currently only has one sample utterance: `"Riproduci {media_type} preferiti"`. Users naturally say things like "suonare la mia musica preferita" which doesn't match and falls through to PlaySongIntent instead.

Add natural Italian phrasing such as:
- "Suona la mia musica preferita"
- "Metti i miei preferiti"
- "Riproduci i miei preferiti"
- "Suona i preferiti"
- "Metti la mia musica preferita"
- "Ascolta i preferiti"

Review other intents (PlaySong, PlayMedia, etc.) for similar gaps in Italian and all other locales.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Italian PlayFavoritesIntent expanded from 1 to 7 utterances (Riproduci, Suona, Metti, Ascolta variants with/without media_type slot). Also enriched 4 other thin Italian intents. Added InteractionModelTests with 60 test cases validating utterance coverage across all 12 locales (309 total tests passing).
<!-- SECTION:FINAL_SUMMARY:END -->
