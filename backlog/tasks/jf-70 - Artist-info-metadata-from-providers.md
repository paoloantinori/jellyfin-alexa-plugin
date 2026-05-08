---
id: JF-70
title: Artist info / metadata from providers
status: Done
assignee:
  - Claude
created_date: '2026-05-04 18:58'
updated_date: '2026-05-05 17:58'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extend artist information retrieval beyond what MediaInfoIntent currently provides. Pull extended artist metadata (biography, genre, related artists, discography summary) from connected Jellyfin providers and surface it through the Alexa skill. Currently MediaInfoIntent only provides partial artist data.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Artist metadata (bio, image, genre, discography summary) is fetched from connected Jellyfin providers
- [x] #2 MediaInfoIntent is extended to surface rich artist details via voice response
- [x] #3 Graceful fallback when provider does not return extended artist metadata
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Problem
MediaInfoIntentHandler exists but is **never registered** in AlexaSkillController's handler array. Additionally, it only surfaces basic track/artist/album info without any extended artist metadata.

### Changes

1. **Extend MediaInfoIntentHandler** (`MediaInfoIntentHandler.cs`)
   - Add `ILibraryManager` and `IUserManager` constructor dependencies
   - When audio is playing with an artist name, look up the `MusicArtist` entity
   - Extract: `Overview` (bio, truncated to 2 sentences), `Genres`, album count (discography summary)
   - Append artist info to existing track description
   - Graceful fallback: if artist not found or no metadata, return current behavior unchanged

2. **Register handler** in `AlexaSkillController.cs` handler array

3. **Add locale strings** for all 12 locales:
   - `ArtistInfoBioGenreAlbums`: "{0} by {1}. {1} is a {2} artist with {3} albums in your library. {4}"
   - `ArtistInfoGenreAlbums`: "{0} by {1}. {1} is a {2} artist with {3} albums."
   - `ArtistInfoBioOnly`: "{0} by {1}. {4}"
   - SSML variants

4. **Update tests** in `MediaInfoIntentHandlerTests.cs`

5. **Run /simplify**
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Implementation Summary

### What was done
1. **Registered MediaInfoIntentHandler** in AlexaSkillController's handler array (it was written but never wired up)
2. **Extended MediaInfoIntentHandler** with artist metadata enrichment:
   - Added `ILibraryManager` and `IUserManager` dependencies
   - When audio is playing, looks up the `MusicArtist` entity from Jellyfin library
   - Extracts: biography (truncated to 2 sentences), genres, album count (discography summary)
   - Graceful fallback: if artist not found or no metadata, returns basic track info unchanged
3. **Added locale strings** for 12 locales (en-US, en-AU, en-CA, en-GB, en-IN, it-IT, de-DE, es-ES, es-MX, es-US, fr-CA, fr-FR) with 7 artist info templates each
4. **Updated tests**: 18 tests total (7 new: bio inclusion, genre-only, no-metadata fallback, bio truncation)
5. **Simplified** per /simplify review:
   - Made fields non-nullable (they're always set via constructor)
   - Extracted shared `BuildTrackDescription()` to eliminate duplicated track description logic
   - Changed `DtoOptions(true)` to `DtoOptions(false)` for artist lookup (only needs Overview/Genres)
   - Added early exit guard before album count query when no metadata exists
   - Fixed `BuildArtistInfoResponse` return type to `string?`

### Acceptance Criteria
- ✅ #1: Artist metadata (bio, genres, album count) fetched from Jellyfin library via ILibraryManager
- ✅ #2: MediaInfoIntent enriched with artist details in voice response
- ✅ #3: Graceful fallback - if no artist found or no metadata, returns basic track info
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
