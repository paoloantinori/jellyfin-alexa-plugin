---
id: JF-672
title: >-
  JF-672 - audiobook chapter play order is default-sort, not chapter order, and
  the paginated chapters query has no explicit tiebreaker
status: To Do
assignee: []
created_date: '2026-09-29 13:43'
updated_date: '2026-09-29 13:44'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-670 -
    JF-670-the-Audiobook-progressive-continuation-is-a-dead-letter-no-fetcher-case-books-truncate-at-the-initial-page-and-PostPlay-AutoPlay-can-append-music-radio-after-a-book.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-670 code-review (high) finding 2, skipped there as beyond the task's decided design (the orchestrator mandated mirroring the initial page's query shape, whose OrderBy is absent).

TWO stacked concerns on the audiobook chapters query (now the ONE shared builder, BuildAudiobookChaptersQuery, used by both the PlayBook initial page and the continuation tail):

1. No explicit order: the query's only sort is Jellyfin's default. Head and tail are two separate LIMIT/OFFSET executions of one shape; chapter rows with equal sort keys have no tiebreaker, so a page-boundary tie could repeat/drop a chapter if the DB's tie order differed between the two executions (same shape, sequential in time, so unlikely but not provable). The album arm added AlbumTrackOrder for exactly the "progressive queue paginates" reason (QueueContinuationFetcher.AlbumTrackOrder, JF-339 AC#3).

2. Default sort is not chapter order: lexicographic SortName puts "Chapter 10" before "Chapter 2". Whether real books suffer depends on whether Jellyfin's default resolves through SortName or IndexNumber for AudioBook children, and whether libraries tag IndexNumber - needs a live probe on the 12.1.0 box before deciding a fix (an explicit (ParentIndexNumber, IndexNumber) order analogous to AlbumTrackOrder is the candidate, but chapters may carry neither).

Fix owner must first verify live: play a 15+ chapter book, compare play order against folder/chapter numbering, check the DB rows' SortName/IndexNumber/ParentIndexNumber. Then decide: explicit order in BuildAudiobookChaptersQuery (single definition makes head+tail flip together) or documented status quo.
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

ORCHESTRATOR GATE-REVIEW ADDENDUM (2026-09-29, same-turn): the shared-shape premise needs one correction - the head (PlayBookIntentHandler initial page) and tail (FetchAudiobookChapters) run DIFFERENT queries today: the tail applies Util.LibraryFilter.ApplyLibraryFilter (TopParentIds) after BuildAudiobookChaptersQuery, the head does not. With no explicit OrderBy the extra constraint can change the engine's natural row order across the page boundary, repeating or dropping a chapter at the boundary. The JF-672 fix (explicit chapter order/tiebreaker) must decide the head-side filter parity in the same change; the two LIMIT/OFFSET executions must be one shape PLUS one filter treatment.
