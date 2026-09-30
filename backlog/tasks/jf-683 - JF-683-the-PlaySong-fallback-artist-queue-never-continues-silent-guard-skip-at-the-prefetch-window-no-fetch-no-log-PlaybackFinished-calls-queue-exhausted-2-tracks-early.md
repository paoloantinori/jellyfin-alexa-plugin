---
id: JF-683
title: >-
  JF-683 - the PlaySong-fallback artist queue never continues (silent guard skip
  at the prefetch window: no fetch, no log; PlaybackFinished calls
  queue-exhausted 2 tracks early)
status: In Progress
assignee: []
created_date: '2026-09-30 16:45'
labels:
  - playback
  - progressive-queue
  - bug
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
  - >-
    backlog/tasks/jf-674 -
    JF-674-stale-queue-continuations-inject-mid-playlist-content-into-a-later-unrelated-single-item-playback-no-queue-identity-validation.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from Paolo's live device round (18:30-18:40, log-verified on the JF-678 build).

THE LIVE EVIDENCE: the phrase "musica di norah jones" was routed by NLU to PlaySongIntent (nondeterministic vs the morning's PlayArtistSongsIntent routing of the same semantic request); PlaySongIntentHandler.PlayArtistSongsFallback -> CrossMediaFallback.BuildArtistSongsResponseAsync logged "PlaySong fallback: fetched 5 songs for artist='Norah Jones'" at 18:30:44. Norah has 13 tracks, artistItems.Count=5 == GetInitialFetchSize(), so the continuation Set gate at CrossMediaFallback.cs:381-396 should have stored (SourceType "Artist", StartIndex 5). At 18:39:55 the PlaybackNearlyFinished fired with current=track 3 of 5 (remaining = 5-2-1 = 2 = the live PrefetchThreshold of 2, so the remaining guard passes IF the index resolves); the handler completed in 10.9ms from the precompute cache hit with ZERO library lookups and NO fetch INFO/WARN line - one of TryFetchContinuationBatch's three silent guards returned: (1) continuation == null (the Set never happened for this key, or something removed it between 18:30 and 18:39), (2) FindCurrentQueueIndex < 0 (the token f692e13c not found in the session queue view), (3) remaining > threshold (only if the queue view disagreed with the 5-item queue the Started handler reported). Corroborating symptom: at 18:40:03 the PlaybackFinished handler logged "queue exhausted, ending session" while TWO tracks remained to play (track 4 started at 18:40:04) - the same queue-view/index confusion shape.

THE WORK: (a) reproduce in a unit test driving BuildArtistSongsResponseAsync (the PlaySong-fallback label path) then a NearlyFinished with a 5-item queue at track 3: assert the fetch fires (or find the failing guard and fix it); (b) OBSERVABILITY: the three silent guards in TryFetchContinuationBatch (continuation null / index < 0 / remaining > threshold) each return without a Debug line - add per-guard LogDebug naming which guard skipped and the values (continuation present?, resolved index, queue count, remaining, threshold) per the debug-logging policy; the "queue exhausted" Finished log should also name its view (queue count + index) so a mis-view is diagnosable from logs alone; (c) fix whichever guard actually fails on the fallback path ( suspicion: FindCurrentQueueIndex's SessionQueue view vs the queue the fallback path built, given the corroborating exhausted-early symptom).

VERIFICATION: the new unit pin (fallback path + NearlyFinished at remaining==threshold fetches and the queue grows past 5); the guard-debug lines visible in a live-shaped run; the existing JF-666 pins and the PlayArtistSongs-path pins stay green; full suite both TFMs. Live spot check on the next device round: the PlaySong-fallback phrase should burn the catalogue like the direct path does.
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
