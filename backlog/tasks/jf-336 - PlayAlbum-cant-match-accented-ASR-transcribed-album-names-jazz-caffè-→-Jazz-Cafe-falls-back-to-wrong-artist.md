---
id: JF-336
title: >-
  PlayAlbum can't match accented/ASR-transcribed album names (jazz caffè → Jazz
  Cafe); falls back to wrong artist
status: Done
assignee: []
created_date: '2026-07-13 05:48'
updated_date: '2026-07-13 06:21'
labels:
  - album
  - playback
  - search
  - phonetic
  - asr
  - handler
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found via on-device testing 2026-07-13 (the user's actual case). After JF-332 fixed routing, PlayAlbumIntent fires correctly for "jazz cafe" on the Echo, BUT the handler then fails to find the album:

Live logs (kitchen Echo, 2026-07-13 07:38):
  intent PlayAlbumIntent, album slot = "jazz caffè" (ER_SUCCESS_NO_MATCH against the AlbumName catalog)
  PlayAlbum: querying Jellyfin searchTerm='jazz caffè', types=MusicAlbum → returned 0 albums
  PlayAlbum: artist fallback found 'Uazz' score=75 → played Uazz (wrong)

Verified root cause (Jellyfin search, admin AND user paolo context):
  searchTerm "jazz cafe"   → 2 albums (the real "Jazz Cafe" records)
  searchTerm "jazz caffè"  → 0 albums   ← what the Echo's ASR sent (Italian accent)
  searchTerm "jazz caffe"  → 0 albums   ← accent-stripped is NOT enough (double-f remains)

The on-device ASR transcribes the Italian pronunciation as "caffè" (double-f + grave accent), but the library album is "Jazz Cafe" (English, single-f, no accent). Jellyfin's search index does not normalize accents or fold doubled consonants, so the searchTerm misses. Accent-stripping (FormD + remove combining marks) produces "caffe" which STILL returns 0 — so the fix must be phonetic (Double Metaphone folds both to "KF"), not accent normalization.

PlayAlbumIntentHandler currently does an exact Jellyfin searchTerm query and, on 0 results, immediately falls back to ARTIST search (BaseHandler.BuildArtistSongsResponseAsync). It has NO fuzzy/phonetic album-name fallback like the artist (ArtistSearch 4-tier) and song (SongNgramIndexService) paths do. So accented/mispelled/transcribed album names fall through to a wrong artist.

Secondary issue: the artist fallback accepted a weak match (score 75) and played 'Uazz' — PlaySong rejects artist matches <85, but PlayAlbum's fallback threshold is more permissive, so the user hears a wrong artist instead of "album not found".

Goal: make PlayAlbum resolve album names the way PlayArtist/FindSong do — phonetically — so spoken variants like "caffè" match "Cafe"; and stop playing a weak wrong-artist match when the album genuinely isn't found.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayAlbumIntentHandler: when the Jellyfin MusicAlbum searchTerm query returns 0, attempt a phonetic/fuzzy match against the user's albums (reuse FuzzyMatcher.FindBestMatchWithScore which already uses Double Metaphone) so ASR transcription variants resolve — e.g. spoken 'caffè' (double-f + accent) matches library 'Cafe'.
- [ ] #2 Verify on-device + via a Jellyfin search repro: 'jazz caffè' (accented) resolves to the 'Jazz Cafe' album (was: 0 albums → artist fallback 'Uazz'). Confirm via profile-nlu + logs that PlayAlbum plays the album.
- [ ] #3 Decide the album-source for fuzzy matching (fetch user's MusicAlbums on the miss, or reuse/extend an index) — keep it cheap (the miss path is cold).
- [ ] #4 Separate concern: PlayAlbumIntentHandler's artist fallback currently accepts a weak fuzzy match (played 'Uazz' at score=75, whereas PlaySong rejects at <85). Align the threshold so a weak artist fallback says 'album not found' instead of playing the wrong artist. Document the chosen threshold.
- [ ] #5 No regression: exact album names still play; in-catalog albums still route to PlayAlbum; PlaySong/PlayArtist unaffected. Run the album NLU/E2E fixtures.
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED + DEPLOYED 2026-07-13 (commit 6b2a09f). Verified via the plugin Simulator with the Echo's exact ASR form:

  Simulator PlayAlbumIntent album="jazz caffè" →
    PlayAlbum: exact search miss, trying phonetic fallback for 'jazz caffè'
    PlayAlbum: phonetic fallback matched album 'Jazz Cafe' score=88   ← was: artist fallback 'Uazz' @75
    PlayAlbum: querying tracks for album='Jazz Cafe'

So the phonetic fallback (FuzzyMatcher / Double Metaphone) correctly bridges the ASR accent+spelling variant ("caffè" → "Cafe", both DM "KF"), and the artist-fallback threshold was raised from GetDefaultThreshold (60) to ContainmentScore (90) so a weak match no longer plays the wrong artist. Unit test FindBestMatchWithScore_AsrAccentAndSpelling_MatchesPhonetically_JF336 pins the foundation. 2489/2489 tests pass.

FOLLOW-ON DATA ISSUE (not a plugin bug): after the fix, "jazz cafe" still doesn't PLAY because both "Jazz Cafe" MusicAlbums in the user's Jellyfin library are EMPTY orphans (0 child Audio tracks, Path=None) — verified via /Items?ParentId=<album> for both album IDs, as admin and as user paolo, and a broad Audio search for jazz/caffe/caffe variants = 0. The plugin correctly responds "no songs in the album". The user must clean up the orphan albums (Jellyfin library cleanup) or add the actual music. This is the reference adoption for JF-337 (9 other handlers with the same phonetic-fallback gap).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
<!-- DOD:END -->
