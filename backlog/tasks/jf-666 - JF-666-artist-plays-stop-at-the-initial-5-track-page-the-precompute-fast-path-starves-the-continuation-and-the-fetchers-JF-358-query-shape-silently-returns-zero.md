---
id: JF-666
title: >-
  JF-666 - artist plays stop at the initial 5-track page: the precompute fast
  path starves the continuation and the fetcher's JF-358 query shape silently
  returns zero
status: To Do
assignee: []
created_date: '2026-09-29 06:01'
labels:
  - playback
  - progressive-queue
  - device-found
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 from Paolo's live report ("musica di norah jones": played 5 tracks then radio-switched to Alice In Chains; expected the whole 13-track catalogue first). Root cause fully traced on the live box (logs + API probes); NOT a deploy regression.

DEFECT 1 (starvation): PlaybackNearlyFinishedEventHandler's precompute cache-hit branch (JF-304) returns BEFORE TryFetchContinuationBatch (the early return at the cache-hit path precedes the fetch call at the full-resolution path), so through the entire initial 5-track page the continuation batch never arrives even when the queue is within the prefetch threshold. The fetch only ever runs on the one full-resolution pass at the last track.

DEFECT 2 (silent zero): QueueContinuationFetcher.FetchArtistSongs uses MediaTypes=Audio on an ArtistIds query, the JF-358 anti-pattern (fixed in PlayArtistSongs' initial fetch, never propagated to the fetcher). On the direct libraryManager path the shape returned ZERO items at StartIndex=5 for an artist with 13 tracks (verified live: the same artist via IncludeItemTypes=Audio returns 13; the HTTP shape with MediaTypes also returns items, so the zero is the direct-path shape, consistent with the documented JF-358 class). Zero items marks the continuation exhausted (silently: no log on the zero path except the store Remove), the handler takes next=null, and PostPlay AutoPlay's genre radio fires from the last track's genre tag.

FIX:
1. Run TryFetchContinuationBatch BEFORE the precompute cache-hit early return (or equivalently at the top of the handler after the state checks), so batches arrive while tracks still play; keep the cache-hit fast path otherwise unchanged.
2. FetchArtistSongs: MediaTypes -> IncludeItemTypes = BaseItemKind.Audio (the JF-358 fix), matching the initial fetch.
3. Add an INFO/WARN log on the zero-items fetch result naming the artist/offset (the silent exhaust made this diagnosis harder than it needed to be).
4. Tests: pin the fetch-before-precompute ordering (a queue at the threshold boundary extends even when the cache hit serves the next track) and the fetcher's corrected query shape (IncludeItemTypes set, MediaTypes absent, on the Artist query; the Album/Playlist fetchers unchanged).

VERIFICATION BAR: live probe after deploy: play an artist with >5 tracks via the simulator, drive the session through track 5's boundary (or await Paolo's device round), and confirm the "Progressive queue: fetched N items for Artist" INFO line appears and the catalogue burns through before any radio transition.
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
