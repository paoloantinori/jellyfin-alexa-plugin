---
id: JF-670
title: >-
  JF-670 - the Audiobook progressive continuation is a dead letter (no fetcher
  case: books truncate at the initial page and PostPlay AutoPlay can append
  music radio after a book)
status: Done
assignee: []
created_date: '2026-09-29 07:35'
updated_date: '2026-09-29 15:33'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 on main (two commits, 72e9f978 + the review round 5dd99996): the Audiobook continuation is no longer a dead letter and books never radio. The fetcher grew the Audiobook arm (FetchAudiobookChapters) running the ONE shared BuildAudiobookChaptersQuery builder the PlayBook head also runs on (ParentId+MediaTypes deliberately, JF-358 governs ArtistIds queries only; no OrderBy deliberately, the initial page's DB order is the chapter order; per-user library filter parity; NRE fallback delegated to the shared SafeGetItemsResult core, which this round also unified). The books-never-radio gate lives INSIDE both radio seeders on the seed source (unbypassable; the seeder's as-Audio cast was the unguarded path, AudioBook deriving from Audio probe-verified on both refs), seeded from the ONE token-first finishing-item resolution, outcome debug-logged per policy; ancestor walk covers item + 3 ancestors (book/part/subfolder/chapter), pinned. 11 pins total incl. the head-side builder pin and the part-nested chapter pin; suite 4746/4746 both TFMs worker-run and orchestrator-verified independently; merged-tree (with JF-664+667) 4752/4752 both TFMs. Gates: Skill simplify (4 applied) + Skill code-review high in-worker (base: 6 findings, 3 applied + JF-672/JF-673 filed; rework: 4 applied) + the orchestrator gate-marker code-review on the base whose F1 (stale-continuation injection: stop a book mid-way, later single song gets mid-book chapters) was investigated by the worker, found to have NO single-fix chokepoint (evidence in JF-674), and filed as JF-674 with the sharper production fact appended (the store site precedes the NativeControlsForBooks split, so VideoApp book plays mint dead entries under the CURRENT production config). LIVE VERIFICATION TRUTH: NativeControlsForBooks=true on the household box means the AudioPlayer book path (this task's surface) is config-gated OFF in production; the unit pins carry the verification and the live check belongs to Paolo's device round with the flag flipped (multi-chapter book plays past the initial page; end-of-book silence under PostPlay=AutoPlay; the deploy's own live probe exercised the shared head query via the PlayBook simulator path).
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
