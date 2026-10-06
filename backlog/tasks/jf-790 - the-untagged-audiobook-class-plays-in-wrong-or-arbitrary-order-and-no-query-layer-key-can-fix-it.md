---
id: JF-790
title: >-
  JF-790 - the untagged audiobook class plays in wrong or arbitrary order, and no
  query-layer key can fix it
status: To Do
assignee: []
created_date: '2026-10-06'
labels: []
references:
  - backlog/tasks/jf-672 - JF-672-audiobook-chapter-play-order-is-default-sort-not-chapter-order-and-the-paginated-chapters-query-has-no-explicit-tiebreaker.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-672 live probe (2026-10-06, evidence in the JF-672 task record): the
production library SPLITS into a tagged class (chapter order correct under the now-explicit
AudiobookChapterOrder, because zero-padded SortName is derived from
ParentIndexNumber/IndexNumber) and an UNTAGGED class whose mis-order is USER-VISIBLE TODAY
and is NOT fixable inside BuildAudiobookChaptersQueryCore, because the rows carry no usable
order key:

- "Thinking Better" (14 chapters named '1'..'14', IndexNumber all NULL): SortName
  lexicographic plays 1, 10, 11, 12, 13, 14, 2, 3, ..., 9. Chapter 10 plays second.
- "The Upside of Irrationality" (100 chapters, SortName AND Name identical on every row,
  IndexNumber all NULL): order is arbitrary today (observed fully scrambled vs the
  001.mp3..100.mp3 file names), and the tie stays a tie under any SortName/Name order.
- "The Art of Deception" (27 chapters, two tagged "parts", SortName tied within each part):
  scrambled within each part.

Query-layer exhaustion (all verified, not assumed): ItemSortBy (reflection-dumped from the
plugin's own packages, both TFMs) has NO Path or Id axis; DateCreated matched file order
only on the already-distinct book, tied 99-way on the 100-chapter book and interleaved
wrong on the parts book; the album-style (ParentIndexNumber, IndexNumber) composite adds
nothing (keys NULL) and would front-load untagged rows ahead of tagged chapters in mixed
books via ASC NULLS FIRST.

The ONLY data that orders these books correctly is the file name (001.mp3..100.mp3,
[01-27]....mp3), which never reaches the DB sort. IN-REPO PRECEDENT ALREADY EXISTS: the
concat endpoint (VideoAudioController.StreamHlsAudiobook, the NativeControlsForBooks=on
path) already re-sorts the unpaged enumeration by trailing filename number
(`_chapterNumberRegex` + the OrderBy at the sortedChapters block, added for exactly this
"Jellyfin doesn't always parse these into IndexNumber" reason, its comment says so). So
the opt-in VideoApp path plays these books CORRECTLY today; the mis-order lives on the
DEFAULT path (NativeControlsForBooks defaults false): the paged AudioPlayer queue
(NowPlayingQueue order, next/previous chapter navigation) and the resume-index math over
the initial page (FindResumeTrackIndex picks trackItems[startIndex]; a scrambled page
picks the wrong chapter for the cold-tracker fallthrough).

Candidate fix shapes, each needing its own design pass before touching code:

1. Queue-side natural-path sort, mirroring the endpoint's proven shape: when the initial
   chapters page comes back with SortName+Name ties (or untagged rows), fetch the book
   unpaged and sort with the SAME trailing-number comparison the endpoint uses (one
   shared helper, not a second regex copy), then page in memory. COST: changes pagination
   semantics (the continuation offsets assume DB-order paging), so it likely means a
   one-shot full fetch for the tied class only, or a precomputed order cached with the
   continuation. Must keep head/tail/unpaged on ONE order (the JF-670/JF-672
   single-definition contract) and must not fork the endpoint's comparator.
2. Library-side remedy (no plugin code): document for the affected libraries that chapter
   files need track tags (IndexNumber) or zero-padded names; Jellyfin re-derives SortName
   from tags on the next metadata refresh. Cheapest, but user-side, not plugin-side.

Not attempted in JF-672 (would change pagination shape; explicitly out of that task's
scope). The probe artifacts are listed in the JF-672 task record.
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
