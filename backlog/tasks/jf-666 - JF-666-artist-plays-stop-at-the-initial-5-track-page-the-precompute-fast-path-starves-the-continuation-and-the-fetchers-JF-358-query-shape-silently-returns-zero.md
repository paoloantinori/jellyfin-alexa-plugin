---
id: JF-666
title: >-
  JF-666 - artist plays stop at the initial 5-track page: the precompute fast
  path starves the continuation and the fetcher's JF-358 query shape silently
  returns zero
status: To Do
assignee: []
created_date: '2026-09-29 06:01'
labels:
  - playback
  - progressive-queue
  - device-found
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 from Paolo's live report ("musica di norah jones": played 5 tracks then radio-switched to Alice In Chains; expected the whole 13-track catalogue first). Root cause fully traced on the live box (logs + API probes); NOT a deploy regression.

DEFECT 1 (starvation): PlaybackNearlyFinishedEventHandler's precompute cache-hit branch (JF-304) returns BEFORE TryFetchContinuationBatch (the early return at the cache-hit path precedes the fetch call at the full-resolution path), so through the entire initial 5-track page the continuation batch never arrives even when the queue is within the prefetch threshold. The fetch only ever runs on the one full-resolution pass at the last track.

DEFECT 2 (silent zero): QueueContinuationFetcher.FetchArtistSongs uses MediaTypes=Audio on an ArtistIds query, the JF-358 anti-pattern (fixed in PlayArtistSongs' initial fetch, never propagated to the fetcher). On the direct libraryManager path the shape returned ZERO items at StartIndex=5 for an artist with 13 tracks (verified live: the same artist via IncludeItemTypes=Audio returns 13; the HTTP shape with MediaTypes also returns items, so the zero is the direct-path shape, consistent with the documented JF-358 class). Zero items marks the continuation exhausted (silently: no log on the zero path except the store Remove), the handler takes next=null, and PostPlay AutoPlay's genre radio fires from the last track's genre tag.

FIX:
1. Run TryFetchContinuationBatch BEFORE the precompute cache-hit early return (or equivalently at the top of the handler after the state checks), so batches arrive while tracks still play; keep the cache-hit fast path otherwise unchanged.
2. FetchArtistSongs: MediaTypes -> IncludeItemTypes = BaseItemKind.Audio (the JF-358 fix), matching the initial fetch.
3. Add an INFO/WARN log on the zero-items fetch result naming the artist/offset (the silent exhaust made this diagnosis harder than it needed to be).
4. Tests: pin the fetch-before-precompute ordering (a queue at the threshold boundary extends even when the cache hit serves the next track) and the fetcher's corrected query shape (IncludeItemTypes set, MediaTypes absent, on the Artist query; the Album/Playlist fetchers unchanged).

VERIFICATION BAR: live probe after deploy: play an artist with >5 tracks via the simulator, drive the session through track 5's boundary (or await Paolo's device round), and confirm the "Progressive queue: fetched N items for Artist" INFO line appears and the catalogue burns through before any radio transition.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EXECUTED 2026-09-29 in worktree agent-adbf59badad45c930 (worktree-branch commit; the live verification round is the task's own VERIFICATION BAR, assigned to the orchestrator post-merge+deploy).

THE ORDERING MOVE (defect 1): TryFetchContinuationBatch moved from AFTER the precompute cache-hit early return to immediately BEFORE the PreEnqueueOnStart gate (after ResolvePlaybackOrder and the JF-574 rehydration, so the fetch sees the rehydrated queue; the sleep-timer check still short-circuits first). The cache-hit branch body is byte-identical, the old call site is deleted, and the ordering constraint lives in a 3-line comment at the new site. Safety of the interleaving: the fetch only APPENDS to session.NowPlayingQueue while CachedNextStillFollowsCurrent validates queue[currentIndex+1], so an append can never invalidate a would-be cache hit. Efficiency verified: the no-continuation common case stays one dictionary read; the library is touched only inside the prefetch window (at most once per batch); exactly one call site remains (grep-confirmed).

THE QUERY FIX (defect 2): FetchArtistSongs MediaTypes=Audio replaced with IncludeItemTypes=BaseItemKind.Audio, mirroring PlayArtistSongs' initial fetch (JF-358), with the live zero evidence in the comment (offset 5 of a 13-track artist, direct ILibraryManager path, 2026-09-29). Album and Playlist arms untouched. The JF-358 sibling sites OUTSIDE the fetcher (YesIntentHandler.PlayArtist, QueryArtistLibrary's tracks listing, SearchMedia's redundant MediaTypes term) were verified in source and FILED as JF-667 the same turn; they are outside this fix's scope.

THE LOG LINE (defect 3): FetchNextBatch (the one choke point after the source switch) logs at WARN on a zero page: "Progressive queue: fetched 0 items for {SourceType} {SourceId} (offset {StartIndex}/{Total}); treating continuation as exhausted", with SourceId per-source labeled (artist/album/playlist + id; playlist falls back to ParentId when PlaylistId is absent). Two review-driven corrections folded in: (a) the WARN and now the success INFO too report the offset CAPTURED BEFORE the fetchers advance StartIndex (FetchArtistSongs marks StartIndex=TotalCount on a short page, which had erased the query offset; the first draft logged 13/13, caught by the pin); (b) the diagnostic is GATED to the three arms that actually query, because the switch's default arm returns an empty page STRUCTURALLY: the Audiobook source type (PlayBookIntentHandler.cs:259 stores it; FetchNextBatch has never had an Audiobook case) would otherwise log a false "library returned nothing" once per long book on the AudioPlayer path. That audiobook continuation gap is pre-existing and deliberately untouched (out of scope).

PIN MECHANISMS (item 4; three NEW tests in ProgressiveQueueTests, zero edits to existing tests):
- PlaybackNearlyFinished_CacheHitWithinPrefetchWindow_StillFetchesContinuation: PreEnqueueOnStart on, a VALID NextTrackPrecomputeCache entry (cached next == live queue successor), a continuation within the prefetch window (3-item queue playing index 0, remaining 2 = the default threshold; the same threshold>=2 dependency the sibling WithContinuation test already carries). The library deliberately does NOT resolve track 2 (GetItemById returns null), so a play directive carrying track 2 WITH THE STORED URL proves the cache-hit branch served, while queue count 3 -> 8 proves the batch landed BEFORE the early return.
- QueueContinuation_ArtistFetch_UsesIncludeItemTypesNotMediaTypes: direct FetchNextBatch call through the file's capturing Callback<InternalItemsQuery> mock (the JF-339 pattern): ArtistIds contains the artist, IncludeItemTypes contains BaseItemKind.Audio, MediaTypes null-or-empty (Jellyfin initializes the array; the contract is "no filter", not null). Also pins the success INFO's PRE-advance offset ("offset 5/13" on a 1-of-5 short page, which advances StartIndex to TotalCount before logging).
- QueueContinuation_ZeroItemFetch_LogsWarningWithQueryOperands: TestCaptureLogger (the JF-546 shared helper; swapped in by the /simplify reuse round in place of a hand-rolled Moq ILogger verification) asserts exactly one Warning carrying "fetched 0 items for Artist", "artist {id}", and "offset 5/13".
RED PROOF: with the two production files reverted to HEAD, all three pins fail 3/3 on BOTH TFMs; restored, 3/3 green on both.

GATES: /simplify 3-angle (parallel agents): findings applied (TestCaptureLogger swap + operand assertions; WARN gated to the querying arms with per-source id; pre-query offset capture; two narrating comment lines trimmed); skipped with reason: sizing the test queue by ProgressiveQueueConstants.GetPrefetchThreshold()+1 (the sibling tests carry the identical threshold dependency; literals are this file's convention). Efficiency angle CLEAN. code-review high: 6 findings; 3 in-diff applied (offset coherence across both log lines, playlist id coalesce, gate/label folded into one nullable sourceId switch); 3 out-of-scope sibling sites verified in source and FILED as JF-667 the same turn.

DoD DISPOSITIONS: #1/#2/#3 green (Debug build 0 errors 0 warnings both TFMs; suite 4730/4730 both TFMs, 4727 baseline + 3 pins, zero pre-existing edits); #4/#5 not applicable (no session attributes, no HttpClient changes); #6 not applicable (no interaction model change); #7 the live E2E round is the task's VERIFICATION BAR assigned to the orchestrator post-merge+deploy (the unit pins cover the handler logic); #8 not applicable (no user-facing strings); #9/#10 done (above).

VERIFICATION TAIL (for the orchestrator's live round): deploy, play an artist with >5 tracks, drive the session to the track-5 boundary; expect the INFO "Progressive queue: fetched N items for Artist (offset ...)" line BEFORE any radio transition (the offset now names the query's start), the catalogue burning through (13/13 for Norah Jones), and no "PostPlay AutoPlay: added" line until the true end. If a zero recurs library-side, the new WARN names the source id and the query offset directly. The audiobook WARN-absence is deliberate (structural default arm; see the JF-667 sibling family).

GATE-MARKER REVIEW ROUND (coordinator findings on the first commit, applied as the review tail commit):
- Finding 1 APPLIED (content-access regression the fix activates): FetchAlbumTracks (both the ParentId query and the AlbumIds fallback) and FetchArtistSongs now apply Util.LibraryFilter.ApplyLibraryFilter(query, pluginUser, libraryManager, logger), the same per-user scope the initial fetches run under (AlbumPlayService.BuildAlbumQuery / SearchService.GetArtistSongsAsync). The handler resolves the plugin user with the radio-path JF-327 shape (DeviceLibraryBindingResolver.ApplyByDevice over _config.GetUserById(session.UserId): the request funnel scopes a clone, GetUserById returns the unscoped instance), and FetchNextBatch gained an optional pluginUser parameter so existing callers compile unchanged. PIN: QueueContinuation_ArtistFetch_AppliesPluginUserLibraryScope (restricted AllowedLibraryIds -> TopParentIds membership on the captured query, mirroring the LibrarySyncServiceTests pattern).
- Finding 3 APPLIED (Alexa-budget exposure on the cache-hit fast path), DESIGN CHOICE: budget wrap, NOT moving the fetch to PlaybackStarted. Rationale: TryPrecomputeNext runs config-gated inside PlaybackStarted (PreEnqueueOnStart), so the moved-trigger option needs a SECOND call site for the knob-off case plus the queue-index machinery duplicated in another handler class; the budget wrap instead gives exact parity with the initial fetch (SearchService wraps the same GetItemList shape in the same budgeted RetryAsync). TryFetchContinuationBatch is now async and wraps FetchNextBatch in RetryAsync("FetchContinuationBatch", cancellationToken), the shared 6s AlexaRequestTimeoutMs budget: a transiently failing batch query stops retrying inside the budget instead of compounding backoff into the ~8s window (the JF-358 class; the budget invariant is globally pinned by RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget). Residual, stated: a single slow-but-successful sync call is interruptible nowhere in this mechanism (equal exposure on the initial fetch), and a terminal fetch failure propagates exactly as it does on the full-resolution path today. PIN: PlaybackNearlyFinished_CacheHitTransientFetchFailure_RetriedWithinBudget (two TimeoutExceptions then the batch; the cached response is still served, GetItemList called exactly 3 times, queue still extended 3 -> 8).
- Finding 4 APPLIED: both the success INFO and the zero-page WARN render TotalCount=int.MaxValue as "end-unknown" (the REAL artist-continuation shape; both creators set int.MaxValue because GetItemList has no count). The zero-log pin now runs the int.MaxValue shape and asserts "offset 5/end-unknown"; the query-shape pin keeps the numeric "offset 5/13" for the finite (Album) shape.
- Finding 5 APPLIED (trivial in scope): the dispatch switch and the sourceId label switch folded into ONE switch returning (Items, SourceId); the null label still gates the zero-page WARN (unknown/default arms are structural, not anomalies).
- Finding 2 FILED same-turn as JF-670: the Audiobook continuation is a dead letter (no fetcher case; books truncate at the initial page on the AudioPlayer path; PostPlay AutoPlay can append music radio after a book). The WARN gate and its comment already documented the structural arm; JF-670 owns the decision (fetcher case vs not storing + suppressing the book radio hand-off).
- Suite after the review fixes: 4732/4732 BOTH TFMs on the final tail state (4727 baseline + 5 JF-666 pins), exit 0, single node; the 5 pins green in isolation on both TFMs; zero pre-existing test edits (the two prior JF-666 pins kept their semantics; only the zero-log pin's fixture moved to the real int.MaxValue shape).
<!-- SECTION:NOTES:END -->
