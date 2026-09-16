---
id: JF-574
title: >-
  Restart wipes the session queue the NearlyFinished resolver reads: false
  queue-exhaustion triggers AutoPlay radio over a surviving persisted device
  queue (Norah Jones incident)
status: To Do
assignee: []
created_date: '2026-09-16 05:53'
updated_date: '2026-09-16 09:21'
labels:
  - bug
  - playback
  - queue
  - restart-recovery
  - autoplay
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-16 07:28 (log-verified): a DLL hot-swap restart at 07:25:44 interrupted a PlayArtistSongs queue 1 minute after it was built (5-item first page, Norah Jones). At the first track's PlaybackNearlyFinished the resolver returned next item=null DESPITE the persisted device queue containing 4 more queued tracks (queue file verified: itemIds 5x Norah Jones, currentIndex=0). Root cause class: the NearlyFinished resolver reads the SERVER SESSION's in-memory NowPlayingQueue, which a restart wipes; the DeviceQueueManager file queue (the crash-recovery mechanism built for exactly this class, JF crash-recovery SetQueue) survives but is NOT consulted as a fallback when the session queue is absent. The false exhaustion then triggered PostPlay AutoPlay (user's per-user setting), which replaced the artist queue with 15 ListenBrainz-pool radio tracks from other artists. FIX DIRECTION: when PlaybackNearlyFinished/PlaybackStopped resolution finds session.NowPlayingQueue empty/null but the persisted device queue for the device exists and contains the current token with remaining items, rehydrate the session queue from the device queue (the crash-recovery path) before declaring exhaustion. Also evaluate: the AutoPopulate trigger should perhaps distinguish 'true queue exhaustion' from 'session state lost across restart'. SECONDARY (separate quality matter, same incident): the ListenBrainz 'similar' pool for Norah Jones returned White Stripes/DMB/Placebo/Cranberries - pool quality bounded by library, verify the similarity provider actually contributed vs a popularity fallback firing.
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
IMPLEMENTED 2026-09-16 (uncommitted, on main working tree): rehydration guard + mirror in PlaybackNearlyFinishedEventHandler. (a) PLACEMENT: a private TryRehydrateSessionQueueFromDevice(session, context, currentToken) on the handler, called in HandleAsync right after the sleep-timer check and BEFORE the precompute gate / TryFetchContinuationBatch / ResolveNextItemId, so every downstream reader of the session queue sees the rehydrated queue; it mirrors through the EXISTING ProgressReporter.MirrorQueueToSession (the same helper the shuffle handlers use). DeviceQueueManager-side placement was rejected (Playback layer would need a MediaBrowser.Controller.Session dependency; the mirror helper already lives handler-side) and middleware placement rejected (every request would pay the check; only this resolver's read treats an empty queue as exhaustion). (b) COHERENCE GUARD, both legs required: session.NowPlayingQueue.Count == 0 (a non-empty session queue belongs to a live playback - a fresh single-song play sets its own one-item queue - so an older persisted queue must never extend it), and the persisted queue CONTAINS the codec-parsed AudioPlayer token (membership, NOT the persisted CurrentIndex/CurrentItemId pointer, which can lag the token across a restart; a stream playing a member of the persisted queue proves that queue is the one the playback was enqueued from). The incident file's shape (currentIndex=0 + playing token as itemIds[0]) satisfies both. Stale-queue hijack and post-restart new-playback hijack are pinned by tests. (c) After rehydration the resolution and TRUE-exhaustion logic (AutoPopulate trigger, PostPlay, episode advance) run unchanged; a rehydrated-but-truly-exhausted queue now also gives PostPlay a correct dedup set (the played items are in the session queue). PlaybackStopped was checked and needs NO change: it has no next-item resolution (position-save only); the false-exhaustion class lives solely in PlaybackNearlyFinished.ResolveNextItemId.

TESTS (red-first, TDD): new Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlaybackRestartRehydrationTests.cs, 3 tests. RED first verified: RestartWipedSessionQueue_CoherentDeviceQueue_ServesNextQueuedTrackNotRadio failed pre-fix with the handler serving a radio-track token instead of the queue's next track (the incident), then GREEN post-fix; the two anti-hijack tests (DeviceQueueNotContainingPlayingToken_StaleQueueDoesNotHijack, NonEmptySessionQueue_IsNeverRehydratedFromDeviceQueue) passed before and after (they pin today's unchanged behavior). Full suite: 3976 passed / 0 failed on BOTH net9.0 and net10.0 (dotnet test, no --no-build). Release build: 0 warnings, 0 errors. DoD: #1-#3 pass; #4-#6, #8 N/A (no session-attr, HttpClient, model, or locale changes); #7 covered at unit level (a mid-restart NearlyFinished is not reproducible via SMAPI simulate-skill); #9/#10 are the orchestrator's review gates on this diff.

SECONDARY INVESTIGATION (pool quality) - FINDING: the task premise is WRONG; there is NO ListenBrainz (or any) similarity provider and NO popularity fallback in this codebase (repo-wide grep for listenbrainz/brainz/lastfm: zero hits in source). The radio pool is a PURE GENRE-MEMBERSHIP RANDOM DRAW: RadioTrackSource.FindRadioTracksAsync (Alexa/Util/RadioTrackSource.cs:54-66) delegates to FindRadioTracksByGenreAsync (RadioTrackSource.cs:83-127), which runs ONE Jellyfin query with Genres = the seed item's Genres array, IncludeItemTypes=Audio, Limit=50, OrderBy=(Random, Ascending) (lines 100-115); callers (PlaybackNearlyFinishedEventHandler.cs:588 and :650, PlayRadioIntentHandler.cs:199) then ShuffleAndCap to 15/20 (Shuffler.ShuffleAndCap). So the White Stripes/DMB/Placebo/Cranberries pool for Norah Jones came from the GENRE TAGS on the seed track (whatever genres the user's library tags carry, e.g. a Rock/Pop-ish tag on a Norah Jones item) matched against every track sharing that tag in the library, randomly drawn and capped. Pool quality is bounded by (1) the seed item's genre tags (a Norah Jones track tagged Rock yields rock radio) and (2) library breadth. The static GenreSimilarityMap (Alexa/Music/GenreSimilarityMap.cs, genre-to-related-genres expansion) is DEAD CODE on this path: GetSimilarGenres has zero production callers (test-only, GenreSimilarityMapTests). If pool quality matters, the lever is either wiring GenreSimilarityMap into the seed genre expansion (cheap, static map) or an actual similarity provider - a separate task, not this bug.
<!-- SECTION:NOTES:END -->
