---
id: JF-489
title: >-
  Calling-word bleed into the MUSICIAN slot: 'chiamato surfer rosa' as an artist
  query dead-ends (strip + album-title retry needed on the album-by-artist path)
status: Done
assignee: []
created_date: '2026-09-04 19:07'
updated_date: '2026-09-04 20:27'
labels: []
dependencies: []
references:
  - Device corr=f919db65 (2026-09-04)
  - JF-469 (the album-slot calling-word strip this extends)
  - 'JF-479 (the musician-absorption shape, different)'
  - TryStripLeadingAlbumCallingWord in BaseHandler
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 device test (corr=f919db65): 'cerca un album chiamato surfer rosa' arrived as PlayAlbumIntent with slots album=EMPTY, musician='chiamato surfer rosa'. The NLU put the calling-word AND the title into the MUSICIAN slot (a different theft shape than JF-469's album-slot bleed and JF-479's musician-absorption). The JF-469 strip only fires on the album-title path (raw album slot value with a calling-word prefix on a raw miss); here the album slot is empty so the strip was never reached. The handler went down the album-by-artist path, found no artist named 'chiamato surfer rosa', and dead-ended with NotFoundAlbumByArtist.

Fix shape (handler-side, mirroring the JF-469 raw-first discipline): when the musician slot value starts with a calling-word prefix (the same TryStripLeadingAlbumCallingWord predicate) AND the album slot is empty, the stripped remainder is almost certainly an album TITLE the user asked for, not an artist name. The album-by-artist path should strip the calling word and try the remainder as an album title first (one bounded indexed retry, the JF-383 pattern): a hit plays the album; a miss falls through to the existing artist search with the STRIPPED value (searching for an artist named 'chiamato surfer rosa' is guaranteed garbage). The raw-first safety applies: if the stripped value doesn't start with a calling word, behavior is byte-identical.

Acceptance criteria:
- Unit: PlayAlbum album=empty musician='chiamato surfer rosa', library has the album -> plays the album (via the stripped album-title retry).
- Unit: musician without a calling-word prefix -> the existing album-by-artist path byte-identical.
- Unit: musician with a calling-word prefix but no album AND no artist match -> the clean not-found naming the stripped value, never 'chiamato X'.
- Device re-verification: 'cerca un album chiamato surfer rosa' in-session plays from Jellyfin.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit dc6f23c1). Guard before the musician branch: when the musician value starts with a calling word and the album slot is empty, the stripped value is retried as an ALBUM title (one bounded BuildAlbumQuery). A hit plays the album and clears the musician slot (the JF-471/473 artist gates stay out of a title query's way); a miss continues the artist search on the stripped value. The BaseHandler TryStripLeadingAlbumCallingWord doc carries the sanctioned-deviation paragraph (musician-slot caller skips the raw-first leg for the artist query; raw-artist reachability via the JF-381 containment band). LIVE-VERIFIED on minix after deploy: hit path 'chiamato surfer rosa' logs the retry, returns 1 album, skips the re-query, plays Surfer Rosa; miss path 'chiamato xyzzyfoo' returns the clean not-found naming the stripped artist after full tier search. Device test card item: 'un album chiamato <titolo>' must play the album.
<!-- SECTION:FINAL_SUMMARY:END -->
