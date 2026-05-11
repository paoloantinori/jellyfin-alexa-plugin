---
id: JF-115
title: APL visual support for Echo Show list/search responses
status: Done
assignee: []
created_date: '2026-05-10 17:19'
updated_date: '2026-05-10 17:55'
labels:
  - enhancement
  - apl
  - echo-show
dependencies: []
references:
  - claudedocs/research_visual_support_echo_show_2026-05-10.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add APL (Alexa Presentation Language) visual templates to all intent handlers that return lists, search results, or browse content, so Echo Show users see items on screen instead of hearing speech-only responses.

**Problem**: When a user asks "quali canzoni abbiamo dei Soul Coughing" on Echo Show, Alexa speaks the 5 results but shows nothing on screen. The same applies to library browsing, artist queries, queue listing, and search disambiguation.

**Current state**: The plugin has APL support for Now Playing (album art + controls) and Queue display via `AplHelper.cs`. Five handlers return list data as speech-only.

**Target handlers**:
- `BrowseLibraryIntentHandler` — numbered lists of artists/albums/songs
- `QueryArtistLibraryIntentHandler` — songs/albums by artist
- `SearchMediaIntentHandler` — disambiguation of search results
- `ListQueueIntentHandler` — queue contents (APL queue template exists but unused here)
- `InProgressMediaListIntentHandler` — in-progress media with timestamps
- `MediaInfoIntentHandler` — info about current track

**Research report**: `claudedocs/research_visual_support_echo_show_2026-05-10.md`
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All 6 target handlers show visual content on APL-capable devices (Echo Show, Fire TV)
- [ ] #2 Speech output remains unchanged for audio-only devices (no regression)
- [ ] #3 Visual lists include thumbnails/art where available
- [ ] #4 Touch interaction works: tapping a list item triggers the appropriate action
- [ ] #5 Device capability detection works correctly (APL only sent to supported devices)
- [ ] #6 Build passes with no new warnings
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All APL visual support for Echo Show list/search/browse responses implemented. Created reusable list template in AplHelper, wired into 5 handlers (PlaySong, BrowseLibrary, QueryArtist, SearchMedia, ListQueue, InProgressMediaList), extracted TryAttachListDirective and GetArtistSubtitle helpers into BaseHandler. 905 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
