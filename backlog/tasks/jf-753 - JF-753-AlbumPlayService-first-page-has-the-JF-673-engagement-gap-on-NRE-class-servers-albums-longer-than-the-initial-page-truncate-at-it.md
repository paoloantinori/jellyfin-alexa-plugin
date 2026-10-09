---
id: JF-753
title: >-
  JF-753 - AlbumPlayService first page has the JF-673 engagement gap on
  NRE-class servers; albums longer than the initial page truncate at it
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-04 18:32'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-673 -
    JF-673-SafeGetItemsResult-fallback-reports-TotalRecordCountpage-size-so-the-PlayBook-continuation-store-never-engages-on-NRE-class-servers-and-books-still-truncate-at-the-initial-page.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-673 work (2026-10-04): the audiobook head fix (SearchService.UnknownTotal opt-in + PlayBook store engagement) exposed that the ALBUM first page has the identical gap.

EVIDENCE: `AlbumPlayService.cs` line ~705 gates the album continuation store on `albumResult.TotalRecordCount > albumResult.Items.Count`, and the two first-page calls (`AlbumPlayService.cs` ~571 ParentId, ~593 AlbumIds retry) run `SafeGetItemsResult` WITHOUT `unknownTotalOnFallback`. On an NRE-class server the page arrives through the GetItemList fallback, whose total is the page size, so a FULL initial page reads as "complete", the store never engages, and the album truncates at the initial page. The FetchAlbumTracks tail (QueueContinuationFetcher) carries the mirrored wrinkle: its `result.TotalRecordCount == 0` split-album retry check also runs against fallback totals, and it has no end-unknown short-page marking.

FIX SHAPE (mirror the JF-673 audiobook pattern, all four pieces): (1) pass `unknownTotalOnFallback: true` on both album first-page calls; (2) the `== 0` no-songs checks need an end-unknown arm (`UnknownTotal && Items.Count == 0`) like PlayBookIntentHandler's; (3) the store gate becomes the full-page/short-page split (`endUnknown ? Items.Count >= InitialFetchSize : TotalRecordCount > Items.Count`); (4) FetchAlbumTracks learns the end-unknown short-page marking plus the split-album retry condition (`== 0` only meaningful in the known-total regime).

NOT DONE in JF-673 because AlbumPlayService is the queue-continuation lifecycle worker's adjacent surface, not JF-673's assigned area (SafeGetItemsResult + PlayBook store condition only).

RELATED (code-review high on JF-673, findings tracked here 2026-10-04): (1) PlaybackNearlyFinishedEventHandler.cs:395 logs continuation.TotalCount raw, so end-unknown continuations render "2147483647" while FetchNextBatch renders "end-unknown" (pre-existing for artist continuations; fold into the same renderer discipline when touching that file). (2) The advance-or-mark idiom now exists in three fetcher variants (Album plain +=, Artist mark-on-short, Audiobook mark-only-when-unknown); this task's FetchAlbumTracks regime work is the documented revisit trigger to consolidate into one shared helper (JF-673 declined to churn the third variant first). (3) An exact-multiple-of-page-size source ends on a zero-item tail batch that trips the dispatcher's zero-page WARN once; accepted boundary noise, documented at the audiobook tail, same treatment for albums.
<!-- SECTION:DESCRIPTION:END -->

<!-- SECTION:NOTES:BEGIN -->
CLOSED with the full four-piece fix plus the three RELATED items (all assigned here by the filing):

1. Both album first-page calls (`BuildAlbumPlayResponseAsync` ParentId + AlbumIds retry) opt into `unknownTotalOnFallback: true`; the zero checks route through the ONE shared predicate `QueueContinuationFetcher.PageHasNoItems` (the JF-673 PlayBook inline check folded into it same-turn, the /simplify reuse finding); the store gate routes through the ONE shared decision `QueueContinuationFetcher.InitialPageHasMore` (the PlayBook ternary folded in likewise; a second `InitialPageHasMore(int)` overload carries the two artist heads' always-end-unknown form, the /code-review F3 finding). The JF-674 mint (MintedQueueItemIds) is untouched; only the gate above it changed.
2. `FetchAlbumTracks` runs the shared `SafeGetItemsResult` executor (the JF-670 head/tail contract: pre-fix the raw `GetItemsResult` NRE'd on the first batch on NRE-class servers, proven RED by the split-album pin), its split-album retry gained the end-unknown zero-ITEMS arm (`TotalRecordCount == 0` stays the known-total trigger), and its advance routes through the ONE idiom `AdvanceOrMarkExhausted`, the consolidation of all THREE fetcher variants plus (code-review F5) the two playlist cached-slice arms.
3. RELATED (1) DONE: `PlaybackNearlyFinishedEventHandler`'s prefetch-window line renders through the ONE renderer `QueueContinuationFetcher.RenderTotal`, which the dispatcher's own lines and the album head's two page logs also use (the head lines were the /simplify reuse finding: this same change made them sentinel-capable).
4. RELATED (2) DONE: the advance-or-mark consolidation (above), the filing's documented revisit trigger.
5. RELATED (3) DONE: the zero-page WARN documented as accepted boundary noise on `AdvanceOrMarkExhausted`'s doc, extended (code-review F4) to state the album's true boundary cost: the WARN plus ONE empty JF-338 AlbumIds retry query before the mark (the audiobook tail, which has no retry, carries only the WARN).

RED PROOFS (JF-673 precedent, live both TFMs on the unmodified base): the head pin `PlayAlbum_NreFallbackFullInitialPage_StoresEndUnknownContinuation` failed with store NULL (the page-size total read as complete, the exact finding); the tail pin `QueueContinuation_AlbumFetch_EndUnknownShortPage_MarksContinuationExhausted` failed at StartIndex 8 vs UnknownTotal (blind advance); the executor pin `QueueContinuation_AlbumFetch_NreServer_SplitAlbumServedThroughFallbackRetry` failed with the raw-executor NullReferenceException at FetchAlbumTracks. All green post-fix on net9.0 and net10.0.

PINS +7: the three red proofs above, the head guardrails (short page stores nothing; zero tracks on both queries still speaks NoSongsInAlbum through the end-unknown arm), the tail full-page guardrail, and the artist-sentinel roster fact `ArtistContinuationConstructionSites_StoreTheEndUnknownSentinel` (code-review F1: every construction site that sets ArtistId must store the UnknownTotal const; sabotage-RED-verified by storing a real total in CrossMediaFallback, which named BuildArtistSongsResponseAsync).

RESIDUAL (code-review F2, accepted, documented at the retry arm): on a PARTIALLY split album the tail's AlbumIds retry can switch row sets mid-stream (ParentId rows played, AlbumIds rows served at the same offset); already-played ids dedup out and tag-linked tracks below the offset can be skipped. Continuing beats truncating at the exhausted parented rows, and the head carries the same JF-338 tolerance (it retries only on an empty FIRST page).

FILED: JF-757 (the /simplify reuse observation out of scope here: the album-track query shape is hand-kept in four copies, head and tail x ParentId and AlbumIds; a BuildAlbumTracksQuery builder would own it).

Gates: worker Skill simplify (4 angles: reuse 3 findings applied incl. the two shared head predicates; simplification 8 findings, comment-dedup + test hygiene applied, the Theory conversion skipped with the agent's own scope-creep reason; efficiency CLEAN; altitude endorsed the layer with the predicate hoist applied) + Skill code-review high (5 findings: F1 roster fact, F2 documented residual, F3 artist-head overload, F4 honest boundary doc, F5 playlist arms folded; all applied). Suites: 5162/5162 net9.0 AND net10.0 on the final state (main baseline 5155 + 7); Release --no-restore -warnaserror 0 warnings 0 errors.

FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Implementation Notes (closed 2026-10-04)
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Debug 0/0; Release --no-restore -warnaserror 0/0)
- [x] #2 dotnet test passes (5162/5162 net9.0 AND net10.0)
- [x] #3 No new compiler warnings introduced (0 warnings both configurations)
- [x] #4 /simplify passed (4 angles; findings applied, skips reasoned in the notes)
- [x] #5 /code-review high passed (5 findings all applied or documented as accepted residual)
- [x] #6 Red proof: album NRE-fallback full initial page stores the end-unknown continuation, failed pre-fix (store NULL on the unmodified base, both TFMs)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit ac57ba6b + orchestrator tail c30cb72b, --no-ff; the gate-marker's six axes PASS with its three findings applied as the tail, incl. the playlist head's fold through the new two-int overload), combined verification via the worker's 5162/5162 both TFMs plus the tail's Release build and affected classes; deployed in the wave's batched deploy. JF-757 filed by the worker.
<!-- SECTION:FINAL_SUMMARY:END -->
