---
id: JF-499
title: >-
  Episode HLS lifecycle watch items: restart vs _episodeHlsItems, 30-min monitor
  timeout, fast-path FileNotFoundException race, Cleanup missing
  UnauthorizedAccessException
status: Done
assignee: []
created_date: '2026-09-05 20:05'
updated_date: '2026-09-14 22:38'
labels:
  - video
  - hls
  - cache
dependencies: []
references:
  - JF-498
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Below-bar watch items from the JF-498 formal review (2026-09-05, reviewer-invoked tracking rule), all in the new episode HLS endpoint's lifecycle. W1: _episodeHlsItems is process-static, so after a Jellyfin restart cached-episode segment fetches resume growing the audiobook position-tracking file (the GetSegment skip only works within the encoding process's lifetime; benign but the stated rationale only half-holds). W2: MonitorFfmpegHlsAsync's 30-minute timeout kills a slow (NAS-bound) episode remux mid-encode, producing debris mid-playback, and the self-heal re-encode restarts the timeline from 0:00 (this path has no resume slice). W3: fast-path race: a request holding a pre-Cleanup FileInfo while a lock-holder invalidates debris can throw FileNotFoundException from ServePlaylistWithToken: one 500 playlist fetch, self-heals on the Echo's retry. W4: VideoAudioCache.Cleanup catches IOException but not UnauthorizedAccessException; on a permission-denied dir the exception propagates out of the playlist fast path as a 500. None blocks the JF-498 deploy; fix opportunistically (W2 and W3 are the most user-visible).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Code-review gate note (2026-09-10, JF-531): the TryDelete helper (~VideoAudioController.cs:3673) catches IOException only, not UnauthorizedAccessException - now ALSO reachable from the new no-runtime stale-listing delete (JF-531); a permission failure there would 500 the play instead of falling back to the live playlist. Fold into this task's UnauthorizedAccessException sweep when it runs.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED fixed and verified (2026-09-14 night, implemented by delegated agent through two API-connection outages, orchestrator-verified, review-hardened). All four watch items + the JF-531 TryDelete note: W1 the process-static _episodeHlsItems registry deleted, tracking gated on the durable Folder fact (memoization removed in review: GetItemById is the platform's LRU); W2a HlsMonitorTimeoutMinutes is now a no-progress stall budget extended by directory-write evidence (slow NAS encodes survive, hung ones die, entireProcessTree kill); W2b StreamHlsEpisode honors ?start= on every serve path with EXTINF-ACCUMULATING slice arithmetic (the review's P2: copy-mode segments land on the source GOP 4-10s, flat division mis-sliced deep resumes ~1.5x; flat fallback kept, uniform+mixed+fallback tests added); W3 shared TryServeValidatedEpisodeCacheAsync catches FileNotFoundException/DirectoryNotFoundException and falls through to re-encode (Windows dir-gone gap closed); W4 UnauthorizedAccessException swept through the delete/cleanup helpers + the enumeration-level catches. 17 tests; suite 3761/3761 both TFMs; Release -warnaserror 0 warnings. Gates: /simplify four-angle (memoization removed, W3 wrapper shared, required params, AudiobookHlsSegmentSeconds const named with coupling doc, hardenings) + code-review high opus (P2 applied). FOLLOW-UPS: JF-565 filed (the ?start= producer - episode resume plumbing via UserData; the W2b machinery is dormant without it); the full three-serve-helper unification deferred (recorded); MonitorFfmpegAndRemuxAsync keeps its flat 5-min kill (out of scope, recorded); stale parentless-AudioBook tracker residue accepted-and-documented (frozen at upgrade, P3).
<!-- SECTION:FINAL_SUMMARY:END -->
