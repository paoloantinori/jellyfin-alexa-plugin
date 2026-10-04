---
id: JF-753
title: >-
  JF-753 - AlbumPlayService first page has the JF-673 engagement gap on NRE-class
  servers; albums longer than the initial page truncate at it
status: To Do
assignee: []
created_date: '2026-10-04'
labels: []
references:
  - backlog/tasks/jf-673 - JF-673-SafeGetItemsResult-fallback-reports-TotalRecordCountpage-size-so-the-PlayBook-continuation-store-never-engages-on-NRE-class-servers-and-books-still-truncate-at-the-initial-page.md
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 /simplify passed (no blocking cleanups remaining)
- [ ] #5 /code-review high passed (no blocking findings remaining or findings applied/tracked)
- [ ] #6 Red proof: album NRE-fallback full initial page stores the end-unknown continuation, failed pre-fix
<!-- DOD:END -->
