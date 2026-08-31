---
id: JF-427
title: >-
  Indefinite album-by-artist plays an arbitrary release: query has no OrderBy
  ('un disco di X' can pick a live album or single, changes after rescan)
status: To Do
assignee: []
created_date: '2026-08-31 17:20'
labels:
  - code-review
  - playback-quality
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:188
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:509
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
New code-review finding (2026-08-31 second high pass, CONFIRMED by code reading). PlayAlbumIntentHandler.cs:188 (JF-411 indefinite album-by-artist resolution), query built at BuildAlbumQuery:509 with NO OrderBy.

DEFECT: 'un disco di X' (album by artist without title) plays artistAlbums[0] from a query with no sort, so the pick is an arbitrary database row: can be a live album, a BBC radio release, or a single instead of a studio album, and WHICH one can change after a library rescan. The log announces it as a deliberate pick ('picked X (indefinite album-by-artist, JF-411)') with no policy behind it.

FIX SHAPE: add a deliberate ordering to the AlbumArtistIds query for this path. Candidate policies: newest first (DateCreated/ProductionYear), prefer MusicAlbum with highest track count (full releases over singles/EPs), or Jellyfin play-count. Pick one, implement as OrderBy on the query, document the policy in code. Separate from jf-422 (elicit dead-end) which touches the same handler but a different defect.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The indefinite album-by-artist pick is deterministic and defensible: query has an explicit OrderBy (decide: newest release, or studio-album preference, or most-played; record the choice in code)
- [ ] #2 Which album plays does not change after an unrelated library rescan (test asserts stable ordering under row-order shuffling)
- [ ] #3 The announce wording matches the actual selection policy
- [ ] #4 Unit test covers multi-release artists (live + studio + single)
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
