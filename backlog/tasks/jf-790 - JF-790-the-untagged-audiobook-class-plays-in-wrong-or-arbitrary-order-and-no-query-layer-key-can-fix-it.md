---
id: JF-790
title: >-
  JF-790 - the untagged audiobook class plays in wrong or arbitrary order, and
  no query-layer key can fix it
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-07 08:42'
labels: []
milestone: m-18
dependencies: []
references:
  - >-
    backlog/tasks/jf-672 -
    JF-672-audiobook-chapter-play-order-is-default-sort-not-chapter-order-and-the-paginated-chapters-query-has-no-explicit-tiebreaker.md
priority: high
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
- [x] #1 dotnet build passes with 0 errors (Release -warnaserror, BOTH TFMs, re-run after every gate tail)
- [x] #2 dotnet test passes (5502/5502 BOTH TFMs on the final state: the 5484 baseline + 18 new; the first net9.0 full run carried one non-reproducing teardown-phase failure in xUnit's DisposeTestClass, green on both immediate reruns)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean at every stage)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute surface touched; the new CachedTracks entry rides the in-memory QueueContinuationStore, never session attributes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model, locale, or speech change; the hard constraint held)
- [x] #7 E2E test added for new intent or handler logic (N/A for a live E2E: no new intent and no handler routing change, and the DB-order dimension the fix lives on cannot be expressed by simulate-skill; the behavior is pinned by 18 new unit cases, four of them RED-proven on the unmodified tree, both TFMs)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [x] #9 /simplify passed (4 parallel angles; Reuse F1/F2 + Altitude F1 + Simplification F1/F2/F3 applied as the consolidated FetchUnpagedBookAsync / RebaseContinuationOnFullBook / RePageFullBookAt helpers and the named maybe-more bar; two skips reasoned in commit efb49fd6)
- [x] #10 /code-review high passed (6 findings, all dispositioned in commit e353245e: F2/F4/F6 applied as doc-truth/boundary/qualification fixes, F5 skipped with the task-prescribed-shape reason, F1+F3 FILED as JF-813 same-turn)
<!-- DOD:END -->

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

**The fix**: the DEFAULT paged AudioPlayer path now plays the untagged/tie audiobook class in FILE order. The initial chapters page runs through a new detection predicate (`ChapterFileNameOrder.PageDistrustsDbOrder`: every page row lacks IndexNumber, the untagged class whose order rests on lexicographic SortName alone; OR two rows share the full (SortName, Name) key, server whim). On detection the resolver fetches the book once unpaged (the deep scan's own scoped-unpaged shape; no second query when the page already carried the whole book), sorts it with the ONE shared trailing-filename comparator, and serves the queue and the continuation from the sorted list: the continuation carries `CachedTracks` (the playlist arm's in-memory paging, generalized into the ONE `SliceCachedTracks` idiom both arms share), so the tail never returns to the DB order that answered wrong.

**The comparator extraction**: `VideoAudioController`'s private `_chapterNumberRegex` + OrderBy lambda became `Alexa/Util/ChapterFileNameOrder.cs`, and the endpoint now consumes the same helper (`SortByTrailingFileNameNumber`), so the seek-mode resume math over the concat timeline and the queue's next/previous navigation describe the same book. ONE deliberate deviation, documented on the class: the number parses through `int.TryParse`, not the endpoint's throwing `int.Parse` (an ISBN/timestamp-named file would otherwise crash the whole play request; the endpoint inherits the tolerance). Pinned from BOTH directions: helper behavior unit tests plus IL pins that the endpoint and the resolver each call the shared method (a fork reds structurally), plus the queue order tests.

**The design decisions**: (1) the untagged trigger is CLASS-based, not page-order-evidence-based, because the lexicographic death is invisible on page 1 itself (Thinking Better's first five rows 1, 10, 11, 12, 13 are numerically consistent; the death shows at the 14-before-2 boundary beyond the page). The accepted sweep: correct-but-untagged books (zero-padded names) also take the sorted path, where the sort is a no-op (their files agree with their padded names; numberless files keep DB order via the stable sort). (2) The pagination trap (continuation offsets assume DB-order paging) resolved through the CachedTracks composition, mirroring the playlist arm: the continuation pages the SORTED list in memory, never the DB. (3) The tagged class never enters the branch (detection cannot fire on rows carrying IndexNumber with distinct keys): byte-identical DB paging, pinned by the companion test.

**The JF-797 composition**: the sorted full book IS the unpaged list the deep-resume scan wants, so the detected shape runs the ONE resume decision over it directly and the deep-resume gate (JF-581 valve, probes, decision log, deep fetch) stays MOOT: its whole purpose is deciding whether to pay the unpaged fetch, which the ORDER fix already paid. The re-page at a found resume chapter is the deep block's contract, now ONE structural helper pair (`RebaseContinuationOnFullBook`/`RePageFullBookAt`) shared by both arms (the /simplify hoist; enforce-by-comment became structural).

**Red proofs** (unmodified tree 19bab3a1, both TFMs, failure messages recorded): the Thinking Better shape failed on the QUEUE (expected file-chapter 2 at slot 1, actual the lexicographic 10); the Upside identical-names shape and the Art-of-Deception tie shape failed on the LAUNCH token itself (the scrambled first row played); the deep-resume composition shape failed on the queue after the resume chapter (DB order, not file order) and the missing sorted continuation; the tagged companion was green as the invariance baseline (and stays green post-fix as the regression guard). All green post-fix on both TFMs.

**Gates**: /simplify (4 parallel angles; the consolidation findings applied, two skips reasoned; commit efb49fd6). /code-review high (6 findings dispositioned; F1 the shared comparator's tail-key-only semantics (per-part file numbering interleaving, mixed numberless tails) and F3 the fresh-ask cost FILED as JF-813 same-turn; commit e353245e). pa:reflect verdict ALIGNED.

**Suites**: 5502/5502 both TFMs on the final state (5484 baseline + 18 new tests: 5 handler-level in PlayBookChapterFileNameOrderTests, 13 unit-level in Unit/ChapterFileNameOrderTests); Release -warnaserror 0 warnings 0 errors both TFMs. Three existing fixtures gained IndexNumber on their chapters (PlayBookResumeTests.SetupDeepResumeBook, ProgressiveQueueTests' JF-674 theory, PlayBookIntentHandlerTests' four book fixtures): they model tagged-class books (Measure What Matters is tagged in the live census) whose DB-path pins must keep testing the DB path.

**Not deployed** (worker branch only; the orchestrator merges and batches deploys). No locale, model, or speech surface touched. One filing: JF-813.
<!-- SECTION:NOTES:END -->
