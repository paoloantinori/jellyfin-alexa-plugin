---
id: JF-67
title: Add library query intents for searching by artist
status: Done
assignee: []
created_date: '2026-05-04 08:24'
updated_date: '2026-05-04 09:00'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add intents that let users ask questions about their library content, e.g. "Which tracks do we have by Artist X?", "Which albums do we have of Artist X?", "What songs are available from Artist X?". Currently the skill can play content by artist but cannot list/query what's available. This requires new intent handlers that search the Jellyfin library and return a spoken list of results. Consider pagination for large libraries (e.g. "You have 47 tracks by X. Here are the first 5..."). Should support both it-IT and en-US locales.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented QueryArtistLibraryIntent handler with artist-based library querying, pagination support for large results (5 items max), and both en-US and it-IT locale support. Added 8 unit tests, interaction model entries for both locales, and the it-IT YAML template. Handler is registered in AlexaSkillController.
<!-- SECTION:FINAL_SUMMARY:END -->
