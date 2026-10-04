---
id: JF-673
title: >-
  JF-673 - SafeGetItemsResult fallback reports TotalRecordCount=page-size, so
  the PlayBook continuation store never engages on NRE-class servers and books
  still truncate at the initial page
status: Done
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
- [x] #1 dotnet build passes with 0 errors (Debug 0 errors; Release --no-restore -warnaserror 0 warnings 0 errors on the final state)
- [x] #2 dotnet test passes (5132/5132 on BOTH TFMs, net9.0 + net10.0, baseline 5126 + 6 new pins)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (n/a: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (n/a: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (no model or locale change)
- [x] #7 E2E test added for new intent or handler logic (n/a by nature: the NRE-class server shape cannot be forced through SMAPI simulate-skill; the RED PROOF unit pins carry the proof, red pre-fix on the behaviorally-unmodified base and green on both TFMs post-fix)
- [x] #8 Locale response strings added to all 17 locales (no user-facing string change)
- [x] #9 /simplify passed (4 agents: efficiency CLEAN; reuse + altitude applied, the two pre-existing int.MaxValue producer literals converted to SearchService.UnknownTotal in PlayArtistSongsIntentHandler + CrossMediaFallback; simplification skipped with reasons: tail advance/mark divergence is deliberate and the test setup repetition matches the file's per-test inline convention)
- [x] #10 /code-review high passed (4 findings: the album head gap already FILED as JF-753 the same turn; the PlaybackNearlyFinished raw TotalCount log + the three-fetcher advance/mark consolidation + the exact-multiple zero-page WARN noise all TRACKED in JF-753's RELATED notes with the consolidation trigger documented; the last is also documented at the audiobook tail as accepted artist-precedent noise)
<!-- DOD:END -->

ORCHESTRATOR GATE-REVIEW ADDENDUM (2026-09-29, same-turn): the NRE-fallback executor now exists in TWO copies - SearchService.SafeGetItemsResult (the head) and QueueContinuationFetcher.FetchAudiobookChapters (the tail, JF-670). The JF-673 fix must lift ONE shared executor (static, logger-taking) and update both call sites; the fetcher copy's QueryResult TotalRecordCount (items.Count) is currently a dead value the caller never reads (TryFetchContinuationBatch reads continuation.TotalCount), so do not build on it.

UPDATE 2026-09-29 (JF-670 rework round, CR3): the ONE shared executor now EXISTS - SearchService.SafeGetItemsResult(libraryManager, query, logger) is the single static core; both the head collaborator and the fetcher tail delegate to it. This task's remaining substance is the TOTAL semantics only (the fallback reports page-size totals, so the PlayBook store never engages on NRE-class servers).

## Final Summary

Closes the TOTAL-semantics remainder (2026-10-04). `SearchService.SafeGetItemsResult` gains an opt-in `unknownTotalOnFallback` parameter: the NRE fallback then reports `SearchService.UnknownTotal` (int.MaxValue, the ONE end-unknown encoding, lifted from the pre-existing QueueContinuation artist regime) instead of the page size. The default stays OFF because the album count-only caller reads TotalRecordCount as a real count. PlayBookIntentHandler's head opts in, and its store condition gains the end-unknown shape: a FULL initial page means "maybe more" (store engages, TotalCount=UnknownTotal; the tail ends the book on a short page, the FetchArtistSongs precedent) while a short initial page means complete; the zero-check gains an end-unknown arm so the single-file audiobook shape survives UnknownTotal-instead-of-0. FetchAudiobookChapters' tail marks exhaustion on a short page in the end-unknown regime (known-total regime byte-identical), and FetchNextBatch's renderer uses the constant. Both red proofs FAILED pre-fix on the behaviorally-unmodified base (store null on the head pin; StartIndex 8 vs UnknownTotal on the tail pin) and are green on both TFMs. Guardrail pins: short initial page stores no doomed continuation; zero-chapters single-file book still plays; full end-unknown page advances without exhausting. The two pre-existing artist-side int.MaxValue producer literals (PlayArtistSongsIntentHandler, CrossMediaFallback) converted to the constant (byte-identical, closes the one-encoding hole). The sibling album-head gap is FILED as JF-753 with its full fix shape. Suites: 5132/5132 net9.0 and net10.0 on the final state (baseline 5126 + 6); Release -warnaserror clean.
