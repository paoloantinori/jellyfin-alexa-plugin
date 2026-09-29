---
id: JF-670
title: >-
  JF-670 - the Audiobook progressive continuation is a dead letter (no fetcher
  case: books truncate at the initial page and PostPlay AutoPlay can append
  music radio after a book)
status: To Do
assignee: []
created_date: '2026-09-29 07:35'
updated_date: '2026-09-29 07:35'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-666 gate review (finding 2). The Audiobook continuation recorded by PlayBookIntentHandler is a dead letter: QueueContinuationFetcher.FetchNextBatch has NO "Audiobook" case, so the dispatch falls to the default arm which returns an empty page WITHOUT running any query.

FAILURE SCENARIO (NativeControlsForBooks=false, the AudioPlayer book path): a book with more chapters than the initial page stores a continuation (PlayBookIntentHandler.cs around line 254-264: SourceType="Audiobook", TotalCount=bookTracks.TotalRecordCount, StartIndex=page size). At the first prefetch-window PlaybackNearlyFinished, TryFetchContinuationBatch calls FetchNextBatch, gets the structural zero, and QueueContinuationStore.Remove deletes the continuation (JF-666 additionally logs nothing here: the zero-page WARN is deliberately gated to the three arms that actually query, since this zero is structural, not a library anomaly). Consequences: (1) multi-chapter books truncate at the initial page of chapters; (2) with PostPlay=AutoPlay, the queue-exhaustion path then treats the current chapter (an Audio item) like a finished song and music-radio tracks get appended after the book (AutoPopulatePostPlayTracks fires; a book genre tag seeds the radio).

SCOPE NOTES for the implementer: the VideoApp path (NativeControlsForBooks=true) is unaffected (it plays the concat HLS stream and emits no AudioPlayer events). Decide deliberately whether the right fix is a fetcher case (ParentId-scoped Audio query on the book folder, mirroring FetchAlbumTracks' shape plus the per-user library filter JF-666 added to the other fetchers) or simply not storing an unserveable continuation plus suppressing the PostPlay radio hand-off for AudioBook items; both behaviors need a decision owner. The JF-666 WARN gate comment in QueueContinuationFetcher names this arm.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A multi-chapter audiobook played via AudioPlayer (NativeControlsForBooks=false) plays through ALL its chapters, not just the initial page: the Audiobook continuation either gets a fetcher case or PlayBookIntentHandler stops storing a continuation it cannot serve
- [ ] #2 With PostPlay=AutoPlay, the end of a book's initial page must not append music-radio tracks to a book queue: the fix must decide the intended end-of-book behavior (silence, book-genre continuation, or nothing) and implement it deliberately
- [ ] #3 The structural zero (default arm) either becomes a real query or the store entry is never created for this source type; no silent dead letter remains
- [ ] #4 Captured-query or handler-path test pinning the chosen behavior; full suite green both TFMs
- [ ] #5 Live spot check on the 12.1.0 box with a multi-chapter book
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
