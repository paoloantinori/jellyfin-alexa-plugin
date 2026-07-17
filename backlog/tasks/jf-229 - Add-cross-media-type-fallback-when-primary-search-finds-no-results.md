---
id: JF-229
title: Add cross-media-type fallback when primary search finds no results
status: Done
assignee: []
created_date: '2026-05-29 19:23'
updated_date: '2026-05-29 19:49'
labels:
  - enhancement
  - handler
  - search
  - fallback
dependencies:
  - JF-228
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

When Alexa's NLU routes a query to the wrong intent (e.g., "mettere gli strokes" → PlaySongIntent instead of PlayArtistSongsIntent), the handler searches the wrong media type, finds nothing, and returns "no song called gli strokes". The user experience is broken even though the correct content exists in the library.

This is a backend-side defense against NLU misrouting — when the primary media type search yields no results, try other media types before giving up.

## Current behavior
1. User says "mettere gli strokes"
2. Alexa routes to PlaySongIntent (wrong intent)
3. PlaySongIntentHandler searches Jellyfin for songs matching "gli strokes"
4. Finds nothing → "Spiacente, non ho trovato nessuna canzone chiamata gli strokes"

## Proposed behavior
1. User says "mettere gli strokes"
2. Alexa routes to PlaySongIntent (wrong intent)
3. PlaySongIntentHandler searches Jellyfin for songs matching "gli strokes"
4. Finds nothing → **fallback**: search artists, albums, videos
5. Finds "The Strokes" as artist → plays artist songs with appropriate response

## Implementation approach

### Option A: BaseHandler cross-media fallback method
Add a `TryFallbackMediaSearch(string query, MediaType excludeType)` method to BaseHandler that:
- Takes the original query and the media type that already failed
- Searches Jellyfin for other media types using the existing search infrastructure (ArtistIndexService for artists, Jellyfin search API for albums/songs)
- Uses the same fuzzy matching pipeline (including phonetic codes)
- Returns the first match with enough confidence (≥ DefaultThreshold)
- Calling handler decides what to do with the result (play it, or ask for disambiguation)

### Option B: Per-handler fallback chain
Each handler that searches for a specific media type adds its own fallback:
- PlaySongIntentHandler: no songs found → try artists → try albums
- PlayAlbumIntentHandler: no albums found → try artists → try songs
- PlayArtistSongsIntentHandler: no artists found → try songs → try albums

Option A is cleaner — single method, consistent behavior. The handler just needs to call the fallback and handle the result.

### Key design decisions
1. **When to trigger fallback**: Only when primary search returns ZERO results (not when fuzzy match fails — that's already handled by HandleFuzzyMiss)
2. **Response wording**: Tell the user what happened — "Ho trovato l'artista The Strokes invece di una canzone"
3. **Confidence threshold**: Use the same DefaultThreshold (60) for fallback matches
4. **Phonetic matching**: Use the new phonetic-enhanced fuzzy matching in the fallback path
5. **Performance**: Fallback adds one more Jellyfin API call on miss — acceptable since it only fires when primary search failed

## Files likely affected
- `Alexa/Handler/BaseHandler.cs` — add fallback method
- `Alexa/Handler/Intent/PlaySongIntentHandler.cs` — use fallback when no song found
- `Alexa/Handler/Intent/PlayAlbumIntentHandler.cs` — use fallback when no album found
- Possibly `Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs` — use fallback when no artist found
- Locale response strings — add "found as different media type" messages

## Acceptance Criteria
- [ ] PlaySongIntentHandler: when no songs found, tries artist search as fallback
- [ ] PlayAlbumIntentHandler: when no albums found, tries artist/song search as fallback
- [ ] Response clearly tells user what media type was found instead
- [ ] Unit tests for the fallback path
- [ ] No performance regression on the happy path (fallback only fires on miss)
- [ ] Works with phonetic-enhanced fuzzy matching
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added cross-media-type fallback to PlaySongIntentHandler and PlayAlbumIntentHandler. When no results found in the primary media type and no musician slot is filled, handlers try artist search via ArtistSearch.SearchAsync with fuzzy matching. If an artist is found, plays their songs with a FoundArtistInstead announcement in all 17 locales. Added 9 unit tests. Build clean, 2045 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
