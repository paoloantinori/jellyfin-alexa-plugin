---
id: JF-427
title: >-
  Indefinite album-by-artist plays an arbitrary release: query has no OrderBy
  ('un disco di X' can pick a live album or single, changes after rescan)
status: Done
assignee:
  - zai
created_date: '2026-08-31 17:20'
updated_date: '2026-09-01 22:44'
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
- [x] #1 The indefinite album-by-artist pick is deterministic and defensible: query has an explicit OrderBy (decide: newest release, or studio-album preference, or most-played; record the choice in code)
- [x] #2 Which album plays does not change after an unrelated library rescan (test asserts stable ordering under row-order shuffling)
- [x] #3 The announce wording matches the actual selection policy
- [x] #4 Unit test covers multi-release artists (live + studio + single)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-08-31 third code-review pass detail: the resolution fetches ALL the artist's albums with no Limit and no OrderBy, keeps only artistAlbums[0].Name, DISCARDS the BaseItem, then re-queries Jellyfin by that name; the re-query can miss on accent/index normalization and cascade into the no-artistIds full-catalog GetItemList plus library-wide fuzzy match (lines 226-238), all inside the 6s retry budget. Also: the multi-match path ~150 lines below sorts by name before choosing, so the two paths already disagree on selection policy; align both when fixing.

2026-09-01 fix landed: pick policy is MOST TRACKS, then newest ProductionYear, then Name, then Id (total order, rescan-stable). Track count is NOT expressible as a query OrderBy (ItemSortBy enum in SDK 10.11.8 has no track-count/ChildCount member, verified by reflection dump), so the ranking runs in memory over the artist's albums, fed by ONE bounded AlbumIds Audio query (lean DtoOptions). The query ALSO carries an explicit OrderBy (ProductionYear desc, SortName asc) per AC#1, defense-in-depth behind the total in-memory key. The chosen BaseItem is now carried directly instead of re-queried by name, removing the accent-miss cascade into the full-catalog fuzzy scan.

ALIGNMENT OF THE MULTI-MATCH PATH EVALUATED AND REJECTED: the note above asked to align both paths when fixing; the fix deliberately does NOT. The multi-match name sort also orders the disambiguation prompt list, and the track-count policy would add a tracks query to that multi-match hot path where the user explicitly named an album. See the divergence comment at the multi-match branch in PlayAlbumIntentHandler (JF-427).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-427: 'un disco di X' now plays a deterministic, defensible release: the fullest studio recording, stable across rescans.

WHAT CHANGED (commit 0e509d1)
- The indefinite album-by-artist pick policy: most tracks first (full studio releases over singles/EPs/live samplers), tie-broken by newest ProductionYear, then Name, then Id (a TOTAL order: rescan row shuffling can never flip the pick; 6-permutation unit test proves it).
- SDK verified by reflection: ItemSortBy has no track-count/ChildCount member at 10.11.8, so the ranking runs in memory over ONE bounded AlbumIds Audio query (grouped by the track's Album metadata name, so JF-338 malformed-folder albums still count); the query keeps an explicit OrderBy (ProductionYear desc, SortName asc) as defense-in-depth behind the total in-memory key.
- The chosen BaseItem is carried directly: the old re-query-by-name (which could miss on accent/index normalization and cascade into the library-wide fuzzy scan inside the 6s budget) is gone; net one query cheaper for multi-release artists.
- DtoOptions(false) with explicit field disables (reflection-verified: bare false leaves images/userdata JOINs on).
- Divergences documented in code: the multi-match path keeps its alphabetical order (it doubles as the disambiguation prompt order and the tracks query would land on that hot path); the review's dead-OrderBy finding declined with reason (AC#1 mandates the explicit OrderBy; the in-memory order is authoritative).

VERIFICATION
- 4 new facts: multi-release artist (10-track live newer vs 12-track studio vs 2-track single) picks the studio album; all 6 insertion orders pick the same; equal-count tie-break by year; equal-count-and-year by name. Existing 11 PlayAlbum facts green (incl. the JF-411 AlbumArtistIds test). Suite 2821 at the implementer's run, 2823 final; Release 0 warnings.
- Gates: /simplify (4 agents; incl. declining the OrderBy removal with reason) + code-review high (its efficiency finding measured LIVE: the count sweep materializes 1,533 rows for a 107-album artist; mechanism correct, perf filed as JF-443 with the COUNT-only query shape; its distinct-name-prompt-loss finding accepted as intended behavior for an INDEFINITE request: the pick is deterministic, announced, and the old prompt fired only on name collisions; JF-442 filed by the implementer for two below-bar cleanups).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
