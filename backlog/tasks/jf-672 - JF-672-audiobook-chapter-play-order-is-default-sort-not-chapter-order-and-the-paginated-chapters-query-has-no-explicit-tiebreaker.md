---
id: JF-672
title: >-
  JF-672 - audiobook chapter play order is default-sort, not chapter order, and
  the paginated chapters query has no explicit tiebreaker
status: In Progress
assignee: []
created_date: '2026-09-29 13:43'
updated_date: '2026-10-06 05:20'
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

AUDIT UPDATE (2026-10-02): BuildAudiobookChaptersQuery now carries a doc comment asserting the status-quo rationale ("the DB order for this shape IS the book's chapter order"); the probe should confirm or refute that claim explicitly. Head/tail asymmetry confirmed current (tail applies ApplyLibraryFilter at QueueContinuationFetcher.cs:225; the head at PlayBookIntentHandler.cs:168-172 does not).
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

## STEP 1: the live probe (2026-10-06, mandatory, executed before any fix decision)

Server: minix, now Jellyfin **12.2.0** (tag v12.2 on GitHub; the filing's 12.1.0 note was the state at filing). Read-only HTTP probes via `?ApiKey=` (the 12.x auth shape).

### Source-level order truth (read at the exact tags, both shipping lines)

- v12.2 `Jellyfin.Server.Implementations/Item/BaseItemRepository.QueryBuilding.cs` `ApplyOrder`: with `filter.OrderBy` EMPTY and no SearchTerm the method returns `query.OrderBy(e => e.SortName)` (an EARLY return, before the "Add SortName as final tiebreaker" block). So the plugin's no-OrderBy chapters query is ORDER BY SortName ASC with NO tiebreaker.
- v10.11.8 `Jellyfin.Server.Implementations/Item/BaseItemRepository.cs` `ApplyOrder`: the empty-OrderBy branch is the same `query.OrderBy(e => e.SortName)`. DIFFERENCE that shapes the fix: 10.11.8 has NO automatic SortName tiebreaker for explicit axes, v12.2 appends `ThenBy(SortName)` when the explicit axes carry no SortName/Name. An explicit composite must therefore name every axis it wants on BOTH lines.
- Both branches, when the FIRST explicit axis is SortName (or Default), append `ThenBy(e => e.Name)`. So an explicit `[(SortName, Asc)]` yields ORDER BY SortName, Name on both lines: strictly more deterministic than today's empty-OrderBy (SortName alone), and the Name key never reorders real rows (probe: every SortName-tied row set in the library also shares its Name).
- SDK enum census (reflection dump of the plugin's own packages, both TFMs): ItemSortBy carries Default, SortName, Name, DateCreated, ParentIndexNumber, IndexNumber, ... and NO Path/Id axis. There is no query-layer key that orders rows by file order.

### Live library census (383 AudioBook leaves under 15 book folders, grouped by ParentId)

The library SPLITS into a tagged class and an untagged class (no book mixes except one, below):

- TAGGED (default order IS chapter order, because Jellyfin derives SortName as '{ParentIndexNumber:0000} - {IndexNumber:0000} - Name' and the padding makes lexicographic == numeric): Option B (12 ch, pidx+idx), Thinking Fast and Slow (40 ch, idx), Moonwalking with Einstein (92 ch, idx), Measure What Matters (26 ch, idx), Diversity Bonus (12 ch, idx), Just for Fun (5 disks, idx=1 each, Name discriminates).
- UNTAGGED, MIS-ORDERED TODAY (SortName lexicographic death): "Thinking Better" (parent 97ff9867, 14 ch named '1'..'14', idx all NULL): default order observed 1, 10, 11, 12, 13, 14, 2, 3, ..., 9. Chapter 10 plays SECOND. Live user-visible mis-order.
- UNTAGGED, ARBITRARY TODAY (full SortName ties): "The Upside of Irrationality" (parent e9720b84, 100 ch, ALL SortName AND Name identical 'The Upside of Irrationality', idx all NULL): observed default order is fully scrambled vs the 001.mp3..100.mp3 file names (065, 057, 008, 070, 086, ...). DateCreated is a 99-way tie (all 2015-06-08T12:47:56; one outlier 001 at 12:48:48): NO DateCreated determinism, and the outlier even inverts the first file last-ish.
- UNTAGGED-but-part-tagged ties: "Kevin Mitnick, The Art of Deception" (parent 42247a52, 27 ch): idx=1 for files [01-14], idx=2 for [15-27] (two tagged "parts"), SortName '0001 - Part 1' x14 / '0002 - Part 2' x13: within each part the order is scrambled today (05, 02, 03, 14, 06, ...). DateCreated does NOT match file order here either (parallel-ingestion interleaving).
- MIXED tagging exists in exactly one real book: "Thinking, Fast and Slow" carries 40 tagged chapters PLUS one untagged stray row (SortName 'Thinking, Fast and Slow', sorts LAST today because digits < letters). Under a (ParentIndexNumber, IndexNumber) composite, SQLite/MySQL ASC NULLS FIRST would sort the stray row FIRST: a REGRESSION of a currently-correct book. This kills the album-style composite for this library.
- Other untagged tie groups (Manager's Path 11, Think Again 7, Brief History of Time 18, Mismatch 12): SortName distinct per row ('... Part 01'..'Part 11'), so default order follows the zero-padded name and is correct.

Probe artifacts: /tmp/jf672_audiobooks2.json (the 383-row census), /tmp/jf672_upside.json, /tmp/jf672_14book.json, /tmp/jf672_mitnick.json (the three problem books, full rows), /tmp/jf672_books_out.txt (the order-vs-path-vs-DateCreated comparisons).

## STEP 2: the decision (from the probe)

1. The doc claim "the DB order for this shape IS the book's chapter order" is **REFUTED for the untagged class** (Thinking Better plays 1,10,...,14,2..9; Upside plays scrambled; Mitnick scrambles within parts) and CONFIRMED for the tagged class. The comment gets the probe-backed truth.
2. Explicit order added: `AudiobookChapterOrder = [(ItemSortBy.SortName, Ascending)]` in the ONE core (BuildAudiobookChaptersQueryCore), so head, confirm, tail, and the JF-784 unpaged endpoint form all flip together and the order is a pinned contract instead of a per-branch server default (both branches currently happen to make empty-OrderBy mean SortName; the plugin no longer depends on that coincidence). Server-side this yields ORDER BY SortName, Name on both 10.11.8 and 12.2 (first-axis-SortName appends Name): a strict determinism gain over today (SortName alone), zero reordering on real rows (probe: SortName-tied rows share Name in every book).
3. The album-style (ParentIndexNumber, IndexNumber) composite is **REFUTED by the probe**: it fixes zero mis-ordered books (every mis-ordered book is in the untagged class, where both keys are NULL and the order falls through to SortName anyway) and it REGRESSES the one mixed book (NULLS-FIRST front-loads the stray row that sorts last today).
4. DateCreated fallback **REFUTED by the probe**: 99-way tie on the 100-chapter book (no determinism gain), wrong order on Mitnick, matches file order only on Thinking Better.
5. Residual, documented: rows sharing (SortName, Name) (Upside's 100, Mitnick's parts) remain unordered among themselves; no query-layer key can order them (no Path/Id axis in ItemSortBy; DateCreated refuted). The untagged-class fix (a handler-side natural-path sort over a full fetch, or library-side tagging) is FILED as a follow-up task, not attempted here (it would change pagination semantics; out of this task's shape).

## STEP 3: the head/tail asymmetry (the filing's audit note)

**CLOSED by JF-767 at this branch point (evidence, not re-fix needed)**: the audit note's asymmetry (tail filtered at QueueContinuationFetcher.cs:225, head unfiltered at PlayBookIntentHandler.cs:168-172) no longer exists on this tree. All three paged sites route through `BuildScopedAudiobookChaptersQuery` (build + ApplyLibraryFilter in the same call, the JF-666 pairing made structural): the head (PlayBookIntentHandler.cs:178), the YesIntent PlayBook confirm (YesIntentHandler.cs:402), and the tail (FetchAudiobookChapters -> BuildScopedAudiobookChaptersQuery). The gate-review addendum's demand ("one shape PLUS one filter treatment") holds by construction. The JF-784 unpaged endpoint form stays user-less BY DESIGN (token-gated HTTP path, scope rides the JF-309 token; the JF-767 record already carries the row-set residual).
