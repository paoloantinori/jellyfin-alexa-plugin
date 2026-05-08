---
id: JF-41
title: Library browsing intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 14:57'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent to browse the media library by artist, album, or genre. Inspired by Kodi/Kanzi skill.

Support utterances like:
- "What albums do you have by {artist}?"
- "Show me {genre} movies"
- "List artists starting with {letter}"
- "What's in my library?"

Implementation: Query Jellyfin API to list items grouped by the requested dimension. Return top results with brief info. User can then choose to play one of the results. Consider pagination for large libraries.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented BrowseLibraryIntentHandler with category-based browsing (artists/albums/movies/songs/genres) and optional filter. Returns spoken numbered list of up to 5 results. Fixed direct dictionary indexing to use TryGetValue. 8 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
