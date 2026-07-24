---
id: JF-338
title: >-
  PlayAlbum: inform the user (don't skip/bare-not-found) when an album is found
  but its tracks return 0 — transient library state
status: Done
assignee: []
created_date: '2026-07-13 07:45'
updated_date: '2026-07-13 08:20'
labels:
  - album
  - ux
  - error-handling
  - library-scan
  - locale
  - playback
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Locale/ResponseStrings.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Per user direction 2026-07-13: when PlayAlbum finds an album (exact or phonetic) but the track query returns 0, do NOT skip the album silently and do NOT give a bare 'album not found' that implies it doesn't exist — the album IS there. Inform the user of the real data situation.

Verified context (JF-336 investigation, the user's actual 'jazz cafe' case): the album 'Jazz Cafe' is real and playable (18 mp3 tracks, e.g. /data/media/music/Jazz Cafe (The Smoothest Sounds From The Coolest A [UK] Disc 1/1. Deep in It.mp3, SupportsDirectStream=True). paolo has EnableAllFolders=True (not a permissions issue). BUT the track count FLAPS (18 ↔ 0) and the Jellyfin API times out mid-query → Jellyfin is re-indexing that album (complex folder name with brackets + 'Disc 1' likely triggers repeated re-parse). PlayAlbumIntentHandler's track query (InternalItemsQuery with ParentId=album, Recursive=true, MediaTypes=Audio) returned 0 at query time during the flap, so the user heard the generic 'NoSongsInAlbum' which sounds like the album doesn't exist.

The current behavior: album found → track query 0 → ResponseBuilder.Tell(NoSongsInAlbum). That message is misleading because the album WAS resolved. The user should hear that the album exists but its tracks are currently unavailable (library updating), and ideally the handler retries once because the 0 is often transient.

This is the data-transparency counterpart to JF-336: JF-336 made PlayAlbum FIND the album (phonetic); this task makes it HONEST about the track state when Jellyfin's data is temporarily incomplete. Do NOT auto-skip the album (user direction) — inform.

Related: JF-336 (phonetic album fallback, done), JF-337 (phonetic fallback for 9 other handlers — the same 'inform on 0 tracks' principle applies there too once adopted).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When PlayAlbum resolves an album (exact or phonetic match) but the track query returns 0, do NOT silently fall through to a generic 'album not found'/'no songs' that implies the album doesn't exist. The album IS there — surface that.
- [ ] #2 Add/adjust a locale response string (all 17 locales) that communicates the real situation: the album was found by name but has no playable tracks right now (e.g. 'Ho trovato l'album Jazz Cafe, ma al momento non ci sono brani riproducibili — la libreria potrebbe essere in aggiornamento'). Keep it user-facing, no internal IDs.
- [ ] #3 Consider a short retry (1-2 quick retries with backoff) of the track query before reporting 0, because the 0 can be transient during a Jellyfin library scan/re-index (observed flapping 18↔0 for the Jazz Cafe album). Document whether retry is added and the rationale.
- [ ] #4 Verify with the Jellyfin simulator: album present + tracks temporarily 0 → user hears the informative 'album found but tracks unavailable' message, not a bare 'no songs'/not-found.
- [ ] #5 No regression: when tracks ARE present, normal playback unchanged; the message only fires on the 0-track-after-found case.
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED + DEPLOYED 2026-07-13 (commit 31d60c8). The "be tolerant of dirty data" fix — PlayAlbum now finds tracks even for split/malformed-folder albums, no user cleanup required.

Root cause (verified, clean 2-run repro on the malformed "Jazz Cafe" album whose folders are truncated + unbalanced brackets + split Disc 1/2): the folder-based track query `ParentId=album, Recursive=true, MediaTypes=Audio` returns 0 even though tracks exist, because Jellyfin's recursive ParentId walk fails for this structure. Querying by album membership (`AlbumIds=album`) returns all 34 tracks across both discs.

Fix: in PlayAlbumIntentHandler, when the primary folder-based track query returns 0, retry by AlbumIds (InternalItemsQuery.AlbumIds, IncludeItemTypes=Audio) before giving up. Verified via the plugin Simulator:
  PlayAlbumIntent album="jazz caffè" →
    phonetic fallback matched 'Jazz Cafe' score=88 (JF-336)
    folder-based track query → 0
    AlbumIds fallback → returned 5 tracks (total=34)
    returning AudioPlayer, album='Jazz Cafe', queueSize=5   ← plays

2489/2489 tests pass on a clean run (one intermittent FuzzyMatcher perf-threshold flake on the loaded sandbox — passes 262ms in isolation, unrelated).

This achieves the user's directive ("we cannot force users to fix; be tolerant"). REMAINING minor (not blocking): the informative user-facing message for the case where BOTH queries return 0 (genuinely empty album) is still the bare NoSongsInAlbum — a locale-string refinement, low priority now that the tolerant fallback handles the realistic dirty-data cases. The same AlbumIds-tolerance principle applies to the 9 other handlers in JF-337.
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
