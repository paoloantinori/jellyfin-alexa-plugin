---
id: JF-113
title: Improve NLU quality for core music intents in non-Italian locales
status: Done
assignee: []
created_date: '2026-05-09 20:31'
updated_date: '2026-05-10 07:13'
labels:
  - nlu
  - multi-locale
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayArtistSongsIntent, PlaySongIntent, and PlayAlbumIntent have only 2 sample utterances in 11 of 12 locales (all except it-IT). it-IT has 76-236 samples for these same intents.

Current state:
- PlayArtistSongsIntent: 2 samples (all non-IT) vs 236 (it-IT)
- PlaySongIntent: 2 samples (all non-IT) vs 76 (it-IT)
- PlayAlbumIntent: 2 samples (all non-IT) vs 92 (it-IT)

With only 2 samples, Alexa's NLU has very few patterns to match against, leading to poor recognition. Add at least 15-20 diverse sample utterances per intent per locale, covering variations like:
- "play songs by {musician}"
- "play music from {musician}"
- "I want to listen to {musician}"
- "play {album} by {musician}"
- etc.

Discovered in JF-98 audit.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added 20-26 diverse NLU sample utterances for PlayArtistSongsIntent, PlaySongIntent, and PlayAlbumIntent across all 11 non-IT locales. English locales got 69 samples, German 70, Spanish 64, French 64 — up from 2 samples each. Uses natural language variations per language (different verbs, noun forms, with/without musician slot).
<!-- SECTION:FINAL_SUMMARY:END -->
