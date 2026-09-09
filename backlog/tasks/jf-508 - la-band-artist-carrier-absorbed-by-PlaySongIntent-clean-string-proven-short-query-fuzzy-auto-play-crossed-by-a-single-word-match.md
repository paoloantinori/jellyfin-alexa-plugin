---
id: JF-508
title: >-
  'la band' artist carrier absorbed by PlaySongIntent (clean-string proven) +
  short-query fuzzy auto-play crossed by a single-word match
status: To Do
assignee: []
created_date: '2026-09-06 15:25'
updated_date: '2026-09-09 02:30'
labels:
  - nlu
  - fuzzy-matching
  - routing
dependencies: []
references:
  - corr=269e622d
  - profile-nlu clean-string evidence
  - JF-379
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two model/routing findings from the 2026-09-06 Dot session. (A) The artist_carrier does NOT force the artist path: profile-nlu on the CLEAN strings 'suona la band soul coffee' AND 'suona la band soul coughing' both select PlaySongIntent with the whole phrase in the song slot and musician empty; PlaySong's greedy 'Suona {song}' family absorbs carrier+name wholesale (device-evidenced 17:21:39 corr=269e622d: heard 'soul coffee', routed to PlaySongIntent, song='soul coffee', auto-played the fuzzy match 'Starfish & Coffee' with the closest-match announce). The artist_carrier vocabulary exists in the it-IT template but the carrier samples are not winning; investigate the carrier sample shapes (they should be '{imperative} {artist_carrier} {musician}'-style with the catalog-backed musician slot) and strengthen them so a carrier phrase routes to PlayArtistSongsIntent. (B) The short-query fuzzy auto-play bar: a 2-word query where only ONE word matched exactly ('coffee' in 'Starfish & Coffee'; 'soul' matched nothing) crossed the auto-play threshold and played with the closest-match announce. Announced+cheap-to-stop is defensible UX, but a single-word match on a 2-word query auto-playing deserves a threshold check: log/inspect the score that crossed the bar (the announcement path is HandleFuzzyMiss's auto-accept at the user-threshold bar) and consider requiring higher coverage (or full keyword coverage) for queries of <=2 words. Also note the ASR chain that fed it: three hearings of 'coughing' across two devices (coughing/coffin/coffee) = the strongest evidence yet for JF-379's generative phonetics.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EVIDENCE APPENDED (JF-510 e2e triage, 2026-09-06, all profile-nlu CLEAN strings, no session context): the family is broader than the original Dot-session report. (1) 'suona la band radiohead' -> NO selectedIntent (part A confirmed live). (2) 'metti una canzone dei beatles' and 'metti una canzone dei xyzzyfoo' -> PlaySongIntent with song='1 canzone dei X' wholesale (the 'Metti {song_noun} {song}' family absorbs the artist carrier for OUT-OF-CATALOG artists; in-catalog names still win via the JellyfinArtist anchor: 'metti una canzone dei soul coughing' and 'metti una canzone dei pink floyd' -> PlayArtistSongsIntent, the latter filling musician='P!nk floyd' from the catalog's phonetic variant). (3) NEW sub-finding: the 'suona i {artist}' JF-418 nominative-article family is dead in current statistics: 'suona i pink floyd' -> PlayAlbumIntent album='i P!nk floyd' (the AlbumName catalog's phonetic anchor STEALS the artist query), 'suona i radiohead' and 'suona i beatles' -> NO selection ('metti i led zep' still routes, so the metti variant lives). (4) Related album-domain drift: 'riproduci album thriller' and "riproduci l'album thriller" -> NO selection; 'metti il disco thriller' -> PlaySongIntent steal. Catalog versions on the live model at probe time: JellyfinArtist v637, AlbumName v643, SeriesName v73 (the JF-504 movie-carrier additions + repeated catalog re-injections shifted the statistics). E2E impact: 5 e2e_it-IT fixtures + the fast-mode xyzzyfoo param are skip_reason-marked pointing here; pink floyd artist coverage continues on the passing 'metti una canzone dei pink floyd'.

Wording correction 2026-09-07 (from the JF-510 review): 4 of the 5 e2e skip_reason entries point here; the fifth (riproduci album thriller) points at the JF-470 definite-article landscape drift, whose evidence sub-finding 4 below already carries.

PART A MECHANISM FULLY UNDERSTOOD (2026-09-09, template + model + catalog analysis): PlaySongIntent has NO bare carrier (every sample carries a song_noun; 80 samples) - the absorption of 'suona la band X' into song='la band X' is pure STATISTICAL matching against 'Suona la canzone {song}'. The carrier samples ('Suona la band {musician}') exist and lose for NON-KG artists because the musician slot is AMAZON.Musician (free-text + Amazon knowledge-graph entities): a KG-known artist (P!nk floyd->P!nk) anchors the entity and flips routing to PlayArtistSongs; an obscure in-library artist (soul coughing) has NO KG entity, no anchor, and the free-text song slot wins statistically. it-IT has only AlbumName (PlayAlbum) and SeriesName as catalog-backed slot types - NO JellyfinArtist anywhere in the model. THE STRUCTURAL FIX is JF-415's architecture (catalog-backed JellyfinArtist musician slot) EXTENDED TO it-IT: the weekly CatalogSyncTask + phonetic variants would anchor every in-library artist (soul coughing included), which is exactly the anchor the KG provides for free for famous ones. Trade-off to design: catalog-backed slots stop filling for out-of-library names (xyzzyfoo routing dies) - the not-found UX must move (elicitation or a documented no-match). Part A is therefore REASSIGNED to JF-415's scope (it-IT extension, mechanism citation this note); JF-508 keeps part B (the fuzzy bar) and closes with it.
<!-- SECTION:NOTES:END -->
