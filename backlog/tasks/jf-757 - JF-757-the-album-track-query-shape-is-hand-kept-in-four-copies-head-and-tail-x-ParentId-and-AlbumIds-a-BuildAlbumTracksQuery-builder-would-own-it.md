---
id: JF-757
title: >-
  JF-757 - the album-track query shape is hand-kept in four copies (head and tail x
  ParentId and AlbumIds); a BuildAlbumTracksQuery builder would own it
status: Done
assignee: []
created_date: '2026-10-04'
labels: []
references:
  - backlog/tasks/jf-753 - JF-753-AlbumPlayService-first-page-has-the-JF-673-engagement-gap-on-NRE-class-servers-albums-truncate-at-the-initial-page.md
  - backlog/tasks/jf-763 - JF-763-JF-757-round-follow-ups-the-residual-hand-kept-album-track-query-shapes-and-the-headtail-library-scope-asymmetry.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-753 /simplify pass (2026-10-04, reuse angle observation, judged out of that diff's scope): the album-track query initializer exists as FOUR hand-kept copies that must stay in lockstep:

1. `AlbumPlayService.BuildAlbumPlayResponseAsync` first page, ParentId arm (`AlbumPlayService.cs` ~577)
2. Same method, AlbumIds retry arm (~599)
3. `QueueContinuationFetcher.FetchAlbumTracks` tail, ParentId arm (~168)
4. Same fetcher, AlbumIds retry arm (~203)

All four share User/Recursive/IncludeItemTypes=Audio/DtoOptions(true)/OrderBy=AlbumTrackOrder and differ ONLY in the scoping term (ParentId vs AlbumIds) and the paging (Limit=GetInitialFetchSize at the head vs StartIndex/BatchSize at the tail). The repo already codifies the fix pattern: `QueueContinuationFetcher.BuildAudiobookChaptersQuery` (JF-670) is the ONE shared builder the PlayBook head and the audiobook tail both run, with the doc explicitly warning that "a hand-kept copy in the handler would drift exactly like the stale-mirror class that bit the interaction-model docs (anti-pattern 11)". The album path never got the same builder; JF-753's own tail comment invokes the head/tail executor contract, and the four copies are the query-shape half of that same drift risk (the executor half was unified by JF-753: both ends now run SafeGetItemsResult).

FIX SHAPE: add `QueueContinuationFetcher.BuildAlbumTracksQuery(jellyfinUser, albumId, startIndex, limit, byAlbumIds)` (or two overloads) next to `BuildAudiobookChaptersQuery`; route all four sites through it. Behavior byte-identical; the JF-338 split-album retry keeps its two-arm shape, only the initializers collapse.

NOT DONE in JF-753 because the filing's assigned surface was the engagement/regime work (opt-in, gate, marking, consolidation of the advance-or-mark idiom and the renderer); rewriting pre-existing query initializers at all four sites is a separate, purely structural change that deserves its own review pass.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors: solution Release --no-restore -warnaserror built 0 Warnings 0 Errors on both TFMs (final state, after all gate edits)
- [x] #2 dotnet test passes: 5194/5194 net9.0 AND net10.0 (main baseline 5193 + exactly 1 new [Fact]); affected families 123/123 both TFMs post-gate
- [x] #3 No new compiler warnings introduced: -warnaserror together with the project's TreatWarningsAsErrors ran clean
- [x] #4 /simplify passed: 4 angles (reuse, simplification, efficiency, altitude); efficiency CLEAN; applied the test charter-comment accuracy fix (the absolute kind-filter/DTO/paging asserts live inside the pin, not in the literal pins) and the builder doc's accepted-boundary qualifier; filed the VideoAudioController concat arms as JF-763; declined with reason the head RetryAsync-envelope local helper (the filing's fix-shape contract: "only the initializers collapse"; the envelope duplication predates the diff in wider form)
- [x] #5 /code-review high passed: 0 correctness bugs in the consolidation (initializers re-established field-for-field verified independently); 4 findings all landed: the commit-scope fix (JF-763 file committed with the diff), the head/tail library-scope asymmetry verified pre-existing by grep (head track pages never carried ApplyLibraryFilter; the tail arms at QueueContinuationFetcher.cs:239/:277 do) and filed as JF-763 Decision 2, the residual inventory completed (YesIntentHandler.PlayAlbum + AplUserEventHandler added as declined-by-design), the byParentId/byAlbumIds polarity nit recorded in JF-763
- [x] #6 A query-shape pin (head vs tail captured queries identical modulo paging) fails on a seeded divergence and passes on the builder: new pin AlbumTracks_HeadAndTail_ShareOneQueryShapeModuloPaging_BothArms (ProgressiveQueueTests) drives the REAL head (PlayAlbumIntentHandler.HandleAsync) and REAL tail (FetchNextBatch) against a split-album server, captures all four issued queries in call order, and asserts per-arm identity modulo paging; green on the builder, RED on a tail-only OrderBy divergence at the exact Assert.Equal(head.OrderBy, tail.OrderBy) line on both TFMs; plus the filter-field red-proof: sabotage A (folder arm writes AlbumIds) redded 11 head-surface tests on both TFMs (PlayAlbum_TrackQuery_OrdersByDiscThenTrack, PlayAlbum_NreFallbackFullInitialPage_StoresEndUnknownContinuation, the new pin), sabotage B (membership arm writes ParentId) redded QueueContinuation_AlbumIdsFallback_AppliesDiscThenTrackOrder + QueueContinuation_AlbumFetch_NreServer_SplitAlbumServedThroughFallbackRetry + the new pin on both TFMs
<!-- DOD:END -->

## Final Summary

The four hand-kept album-track query initializers collapsed into ONE builder,
`QueueContinuationFetcher.BuildAlbumTracksQuery(jellyfinUser, albumId, startIndex, limit, byAlbumIds)`, placed
next to its JF-670 audiobook twin. Verified identical modulo the scoping term and paging BEFORE extracting (User,
Recursive, IncludeItemTypes=Audio, DtoOptions(true), OrderBy=AlbumTrackOrder shared; head pages 0 +
GetInitialFetchSize with StartIndex previously unset, so the explicit 0 is byte-identical, the JF-670 precedent;
tail pages StartIndex + BatchSize; the tail's ApplyLibraryFilter stays at its site, as before). The unselected
scoping field stays at its constructor default (an explicit empty AlbumIds on the folder arm, or a set ParentId on
the membership arm, would AND a second constraint into the server query and defeat the JF-338 recovery),
documented in the builder doc and pinned by the new lockstep test. All existing pins stayed green UNCHANGED (they
assert captured issued queries, which the consolidation preserves byte-identically; no pin asserted a hand-built
query object, so no test indirection was needed). The new lockstep pin adds the head-vs-tail identity guard (DoD
#6); its division of labor with the existing literal pins is documented in its charter comment. Production
surface: AlbumPlayService.cs head (2 sites) + QueueContinuationFetcher.cs tail (2 sites) + builder; test surface:
ProgressiveQueueTests.cs (+1 Fact, 52/52 in-family). Out-of-scope findings filed same-turn as JF-763 (the
concat-endpoint fold-in decision, the pre-existing head/tail library-scope asymmetry, the residual-shape
inventory). No deploy: production query behavior byte-identical, test-adjacent change only.
