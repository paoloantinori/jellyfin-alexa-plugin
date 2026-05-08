---
id: JF-66
title: Add full examples of localized utterances to README
status: Done
assignee: []
created_date: '2026-05-04 08:24'
updated_date: '2026-05-04 09:02'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a comprehensive section to README.md with examples of all supported voice commands in each locale (starting with it-IT). Show natural language examples for each intent category: playback (play song/album/artist), navigation (next/previous/chapter), favorites, library browsing, etc. Organize by intent category with both imperative and infinitive forms. Reference the YAML templates in Alexa/InteractionModel/templates/ as the source of truth.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Reorganized the README Supported Voice Commands section with side-by-side en-US and it-IT examples for every intent category. Added documentation for all previously undocumented intents (QueryArtistLibrary, sleep timer, mood, radio mode, queue management, voice identification, chapter navigation, recommendations, random play, continue watching, play by genre, play episode).
<!-- SECTION:FINAL_SUMMARY:END -->
