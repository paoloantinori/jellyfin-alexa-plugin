---
id: JF-211
title: 'Add "c''è {query}" pattern to SearchMediaIntent after NLU conflict analysis'
status: Done
assignee: []
created_date: '2026-05-23 17:24'
updated_date: '2026-05-25 10:36'
labels:
  - enhancement
  - interaction-model
  - needs-investigation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem
"c'è {query}" (is there...) is a very natural Italian pattern: "c'è un film che si chiama matrix?" But it risks NLU conflicts:
- "c'è un brano di Radiohead?" could compete with QueryArtistLibraryIntent / PlayArtistSongsIntent
- "c'è una canzone che..." could compete with PlaySongIntent

## What needs investigation
1. Test whether adding `c'è {query}` to SearchMediaIntent causes NLU misrouting
2. If conflicts exist, consider adding disambiguating concrete samples to competing intents
3. May need the same pattern in other locales (e.g. "is there..." in English)

## Acceptance criteria
- [ ] NLU conflict testing with `c'è` utterances against competing intents
- [ ] If safe: add `c'è {query}` to SearchMediaIntent in it-IT (and equivalents in other locales)
- [ ] If conflicts found: document which utterances conflict and propose mitigation
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## NLU Conflict Analysis Results

**HIGH RISK** of NLU capture if `c'è {query}` added to SearchMediaIntent with AMAZON.SearchQuery slot.

### Why: AMAZON.SearchQuery is greedy
- Adding `c'è {query}` would capture ALL `c'è` utterances, not just generic searches
- Competing intents use typed slots (AMAZON.Musician, AMAZON.MusicRecording, AlbumName) which lose against SearchQuery
- Existing `c'è` patterns (MediaInfoIntent, QueryRecentlyAddedIntent, ListQueueIntent) are slotless and wouldn't conflict directly, but the new pattern would steal their traffic

### Safer alternative: intent-specific `c'è` patterns
- `c'è una canzone chiamata {song}` → PlaySongIntent
- `c'è musica di {musician}` → PlayArtistSongsIntent
- `c'è un album chiamato {album}` → PlayAlbumIntent
- `c'è {query}` → SearchMediaIntent (only as fallback for truly generic `c'è` queries)

### Recommendation
Add specific typed patterns first, then add `c'è {query}` as last resort. The typed slots give Alexa's NLU better signals for intent resolution.

**Status: Investigation complete, awaiting design decision on approach.**
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
NLU conflict analysis completed. Chose intent-specific approach over greedy SearchQuery. Added 13 c'è patterns across PlaySongIntent (6), PlayArtistSongsIntent (3), PlayAlbumIntent (3), and SearchMediaIntent (1 generic fallback). Add 3 NLU test cases. Build 0 errors, 1875 tests pass, models valid.
<!-- SECTION:FINAL_SUMMARY:END -->
