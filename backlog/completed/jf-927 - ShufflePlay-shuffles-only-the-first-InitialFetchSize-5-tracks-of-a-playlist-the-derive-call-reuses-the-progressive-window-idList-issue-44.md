---
id: JF-927
title: >-
  ShufflePlay shuffles only the first InitialFetchSize (5) tracks of a playlist:
  the derive call reuses the progressive window idList (issue #44)
status: Done
assignee: []
created_date: '2026-11-15 13:46'
updated_date: '2026-11-15 15:56'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/44'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
VERIFIED against source + config (2026-11-15, issue #44 by znouetp, plugin 1.0.2.2, JF 12.4): "Alexa ask Jellyfin Player to shuffle the playlist X" only shuffles the FIRST 5 songs; the rest of the playlist plays in original order (the reporter's own wording: "it seems to play as it normally would").

ROOT CAUSE (source-verified): AlbumPlayService.BuildPlaylistPlayResponseAsync builds the device queue from the PROGRESSIVE WINDOW ONLY: AlbumPlayService.cs:1276 `playlistItems = allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize())` (default 5, ProgressiveQueueConstants.cs:11), then :1303 builds `idList` from that window and :1309 `queueManager.DeriveShuffledQueue(idList, rng)` derives the shuffle snapshot from the WINDOW, and :1353 CommitShuffledQueue installs it. The continuation store (:1365-1387) then appends tracks 6+ in the playlist's ORIGINAL order on PlaybackNearlyFinished. Net: a random track among the first five, the window's remainder, then everything else in order. DeriveShuffledQueue/CommitShuffledQueue themselves have NO cap (DeviceQueueManager.cs:1978/2002 shuffle whatever they are given); the windowing is accidental reuse of the ordered path's progressive pattern, not a recorded scope decision (the JF-713 commit 9fe2c4fd introduced the derive call reusing the windowed idList; no cap rationale anywhere in its record).

FIX SHAPE (minimal): on the shuffle arm, derive the snapshot from the FULL allTracks id list (and commit it), not the window; the ordered arm keeps the progressive window + continuation. With the full list committed the continuation store must NOT be seeded for that launch (TotalCount == queue size; the JF-674 MintedQueueItemIds identity question disappears). Verify interplay: PlaybackNearlyFinished's continuation/resolver paths must treat a committed full shuffle as already-complete (the maybe-more bar reads StartIndex/TotalCount; with no store entry there is no fetch); ShuffleOn mid-playback derives from the live queue so it inherits the fix; unit pins in the JF-713 DeriveAndCommit_* family extend to a >window playlist asserting the committed queue length == full count and PlaybackOrder=Shuffle.

VERIFY ON REAL ARTIFACT after the fix: simulator ShufflePlay on a big playlist on minix (queue length log line at AlbumPlayService.cs:1325 says queueSize=queueItems.Count, which itself will read 5 pre-fix: extend the log or assert via the persisted device queue JSON), plus the existing unit battery.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in commit f236ba35 (2026-11-15), deployed to minix same day and live-verified on both arms. THE FIX: the shuffle arm of AlbumPlayService.BuildPlaylistPlayResponseAsync derives its snapshot from the FULL allTracks id list (deduped first-occurrence; the first-match queue advance would otherwise cycle duplicate tracks forever), the continuation lifecycle became an unconditional Remove before the arm split (both arms replace the device queue wholesale; the ordered small-playlist launch previously left an earlier play's entry alive past the subset-shaped JF-674 gate), the ordered arm alone re-seeds from its window edge, and the shuffle arm seeds full-list session metadata so the mirror keeps PlaylistItemId beyond the window. FINDINGS DISPOSITION (code-review high, 5): 1 applied (dedupe), 2 applied (unconditional Remove + pin), 3 applied (metadata seed), 4 ACCEPTED EDGE (unbounded full commit at pathological sizes: correct playback at any size, ~780KB debounced persist at 10k tracks, efficiency review quantified and declined a cap as semantic truncation), 5 applied (NotEqual anti-identity pin). The sibling defect class via mid-playback ShuffleOn is JF-928 (different entry point, not reachable from this path). TESTS: +5 pins (full-list shuffle + anti-identity, stale-continuation clear, ordered window+seed non-regression, duplicate-once, small-ordered stale drop); TestHelpers.SetupPlaylist gained trackCount (default 5); suite 6159/6159 per TFM, build 0 warnings. LIVE VERIFY (real server, real queue file): shuffled launch of the 12-track Dinosaur Jr playlist committed queue_*.json with 12 distinct ids, playbackOrder Shuffle, 12-entry original snapshot, order != source, first item == the directive's stream id, log queueSize=12 (pre-fix 5); ordered launch on the same playlist committed 5 ids, Default order, no shuffle state. Catalog users get the fix in the next tagged release; the issue #44 reporter can be offered a dev build.
<!-- SECTION:FINAL_SUMMARY:END -->
