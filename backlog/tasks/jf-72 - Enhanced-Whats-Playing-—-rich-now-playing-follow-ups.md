---
id: JF-72
title: Enhanced "What's Playing" — rich now-playing follow-ups
status: Done
assignee:
  - Claude
created_date: '2026-05-04 19:01'
updated_date: '2026-05-05 18:36'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enhance MediaInfoIntent with comprehensive now-playing follow-up queries. Users should be able to ask about the currently playing item and get rich metadata responses. All metadata is available from the Jellyfin API — the enhancement is about making the skill conversational around now-playing context.

Supported queries:
- "What song is this?" → title + artist
- "What album is this from?" → album name
- "Who sings this?" → artist name
- "What year was this released?" → year
- "Tell me about this artist" → biography snippet
- "How long is this song?" → duration
- "What genre is this?" → genre
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 "What song is this?" returns title + artist
- [x] #2 "What album is this from?" returns album name
- [x] #3 "Who sings this?" returns artist name
- [x] #4 "What year was this released?" returns release year
- [x] #5 "Tell me about this artist" returns a biography snippet from Jellyfin provider metadata
- [x] #6 "How long is this song?" returns track duration
- [x] #7 "What genre is this?" returns genre
- [x] #8 Conversation context is maintained so follow-ups reference the current track without re-prompting
- [x] #9 Graceful response when requested metadata field is unavailable
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Architecture: Slot-based MediaInfoIntent enhancement

**Key insight**: Add an optional `media_info_type` slot to the existing `MediaInfoIntent`. When no slot is provided (backward compat), behavior stays the same. When a slot is provided, return targeted metadata.

### Steps:

1. **Interaction models** (all 12 locales): Add `MediaInfoType` custom slot type + slot `media_info_type` to MediaInfoIntent + new utterances
2. **Handler** (`MediaInfoIntentHandler.cs`): Read slot, dispatch to targeted response methods
3. **Locale files** (all 12): Add response strings per info type + "field unavailable" fallback
4. **Tests**: Unit tests for each info type response
5. **`/simplify`**: Run simplify skill
6. **Commit**

### Session context (AC #8): Already handled — Jellyfin session's `NowPlayingItem` persists across follow-up turns within the same Alexa session.

### Graceful fallback (AC #9): Each targeted method checks if the field exists and returns a "not available" string if missing.
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Enhanced MediaInfoIntent with slot-based follow-up queries. Added `media_info_type` slot supporting 7 query types (title, album, artist, year, duration, genre, biography) across all 12 locales and interaction models. Each query type returns targeted metadata with graceful unavailable fallbacks. Backward compatible — no slot yields original full now-playing response. Extracted `BuildNowPlayingResponse` to eliminate code duplication between default and fallback paths. Added 20 unit tests covering all query types and edge cases.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
