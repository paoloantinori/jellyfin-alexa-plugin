---
id: JF-75
title: Play by decade / era
status: Done
assignee: []
created_date: '2026-05-04 19:01'
updated_date: '2026-05-05 21:07'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add intent to play music filtered by decade or era. Users should be able to say "Play music from the 80s", "Play 90s rock", "Play songs from 2000". Jellyfin API supports year-based queries on library items, making this straightforward to implement. A fun browsing capability that suits voice interaction well.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Intent handles utterances like 'Play music from the 80s', 'Play 90s rock', 'Play songs from 2000'
- [ ] #2 Decade/era slot extracts the time period from user speech
- [ ] #3 Jellyfin API year-based queries filter results to the requested era
- [ ] #4 Optional genre + decade combination is supported
- [ ] #5 Graceful response when no items match the requested era
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayByDecadeIntent with full decade/era music playback support. Handler parses word-form decades (eighties, nineties) and numeric forms (80s, 2020s) into year ranges using InternalItemsQuery.Years. Includes optional genre filtering (en-US only due to Alexa AMAZON.SearchQuery constraint). Decade custom slot type added for both en-US and it-IT locales. 35 unit tests covering word forms, numeric forms, edge cases. en-US model deployed and NLU tests pass. it-IT deployment blocked by pre-existing PlayLastAddedIntent slot conflict (separate issue). Also fixed missing await bug in MediaInfoIntentHandler.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
