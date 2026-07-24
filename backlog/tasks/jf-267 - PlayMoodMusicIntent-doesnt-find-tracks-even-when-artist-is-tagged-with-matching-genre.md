---
id: JF-267
title: >-
  PlayMoodMusicIntent doesn't find tracks even when artist is tagged with
  matching genre
status: Done
assignee: []
created_date: '2026-06-06 13:46'
updated_date: '2026-06-06 14:08'
labels:
  - bug
  - mood
  - handler
  - genre
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User tagged the artist "L'altra" with genre "rilassante" in Jellyfin, but "mettere musica rilassante" (PlayMoodMusicIntent) still returns "Non ho trovato musica rilassante nella tua libreria."

**Reproduction:**
1. Tag an artist (L'altra) with genre "rilassante" in Jellyfin
2. "Alexa, chiedi a mia collezione di mettere musica rilassante"
3. Response: "Non ho trovato musica rilassante nella tua libreria."

**Expected:** Should find tracks by L'altra since that artist is tagged with genre "rilassante".

**Investigation needed:**
1. Check if PlayMoodMusicIntentHandler queries by `Genres` filter — does it search artist genres or track genres?
2. Jellyfin may tag genres at artist level but the handler queries Audio items which may not inherit the artist's genre
3. Check if the fallback (using mood word "rilassante" as genre name) is actually being reached — the MoodGenreMap substring match might not work for Italian words
4. Check jellyfin logs for the actual query being sent (enable Debug logging)
5. Verify in Jellyfin API directly: does a genre filter for "rilassante" return L'altra tracks?

**Possible root causes:**
- Artist-level genre vs track-level genre mismatch
- The handler queries Audio items but genre is on the MusicArtist
- Genre name case sensitivity ("Rilassante" vs "rilassante")
- The MoodGenreMap substring match fails for "rilassante" (it doesn't contain any English key)
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
Fixed PlayMoodMusicIntent for Italian moods and artist-genre tagging.

**Root cause (two parts):**
1. MoodGenreMap only had English keys. Italian words like "rilassante" fell through to raw-mood-as-genre fallback, finding nothing.
2. Handler only searched BaseItemKind.Audio with Genres filter, but Jellyfin often tags genres at artist level, not track level.

**Fix:**
- Added `LocalizedMoodMap` (16 Italian → English mood keys) with 5-step resolution chain in `ResolveGenres`
- Added `SearchByArtistGenreAsync` fallback: when track-genre returns nothing, queries MusicArtist by genre → their Audio children
- Added debug logging for mood resolution and fallback trigger

**Tests:** 6 new tests (4 Italian mood resolution, 1 end-to-end Italian mood, 1 artist fallback). All 2225 tests pass.

**Commit:** ca6f61d
<!-- SECTION:FINAL_SUMMARY:END -->
