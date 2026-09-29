---
id: JF-673
title: >-
  JF-673 - SafeGetItemsResult fallback reports TotalRecordCount=page-size, so
  the PlayBook continuation store never engages on NRE-class servers and books
  still truncate at the initial page
status: To Do
assignee: []
created_date: '2026-09-29 13:44'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-670 -
    JF-670-the-Audiobook-progressive-continuation-is-a-dead-letter-no-fetcher-case-books-truncate-at-the-initial-page-and-PostPlay-AutoPlay-can-append-music-radio-after-a-book.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-670 code-review (high) finding 3, skipped there as beyond the minimal fix (needs a SafeGetItemsResult fallback signal or end-unknown store semantics).

MECHANISM: on servers where Jellyfin's GetItemsResult NREs in its Count() translation for a query shape, SearchService.SafeGetItemsResult falls back to GetItemList and constructs QueryResult(startIndex, items.Count, items) - i.e. TotalRecordCount = PAGE SIZE, not the library total. PlayBookIntentHandler stores the Audiobook QueueContinuation only when `bookTracks.TotalRecordCount > bookTracks.Items.Count` (PlayBookIntentHandler.cs ~line 249), so on the fallback path a FULL initial page yields TotalRecordCount == Items.Count, the store is skipped, and the JF-670 continuation tail never engages: books longer than the initial fetch size STILL truncate at the initial page, exactly on the servers the NRE guard exists for.

The tail arm has a mirrored wrinkle: FetchAudiobookChapters' own NRE fallback (QueueContinuationFetcher.cs) also loses the total, so the store-removal check (`StartIndex >= TotalCount`) runs against a page-size TotalCount.

CANDIDATE FIXES (owner decides): (a) expose a fallback-engaged signal from SafeGetItemsResult (API change; album paths share it) and store an "at least one more page" continuation with artist-style short-page end detection (FetchArtistSongs precedent: TotalCount=int.MaxValue + items.Count < BatchSize marks the end, keeping the zero-page WARN meaningful); or (b) probe live whether the NRE class fires at all for the ParentId+MediaTypes book shape and downgrade to documented status quo if it never fires. NOTE the class is documented for ArtistIds+PopularitySort and UNOBSERVED for the book shape - verify before building.
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

ORCHESTRATOR GATE-REVIEW ADDENDUM (2026-09-29, same-turn): the NRE-fallback executor now exists in TWO copies - SearchService.SafeGetItemsResult (the head) and QueueContinuationFetcher.FetchAudiobookChapters (the tail, JF-670). The JF-673 fix must lift ONE shared executor (static, logger-taking) and update both call sites; the fetcher copy's QueryResult TotalRecordCount (items.Count) is currently a dead value the caller never reads (TryFetchContinuationBatch reads continuation.TotalCount), so do not build on it.

UPDATE 2026-09-29 (JF-670 rework round, CR3): the ONE shared executor now EXISTS - SearchService.SafeGetItemsResult(libraryManager, query, logger) is the single static core; both the head collaborator and the fetcher tail delegate to it. This task's remaining substance is the TOTAL semantics only (the fallback reports page-size totals, so the PlayBook store never engages on NRE-class servers).
