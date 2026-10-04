---
id: JF-674
title: >-
  JF-674 - stale queue continuations inject mid-playlist content into a later
  unrelated single-item playback (no queue-identity validation)
status: In Progress
assignee: []
created_date: '2026-09-29 15:02'
updated_date: '2026-10-04 13:13'
labels:
  - playback
  - progressive-queue
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-670 -
    JF-670-the-Audiobook-progressive-continuation-is-a-dead-letter-no-fetcher-case-books-truncate-at-the-initial-page-and-PostPlay-AutoPlay-can-append-music-radio-after-a-book.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn (the promise made when the JF-670 F1 finding came back: "we will file the queue-lifecycle task"). Filed by the orchestrator; evidence gathered by the JF-670 worker.

THE FINDING (JF-670 gate-review F1, medium): a stale QueueContinuation outlives the playback it was minted for and injects its content into a LATER, unrelated single-item playback. Concrete new symptom: play a long audiobook, stop mid-book (the store entry is NOT removed on PlaybackStopped), then play ONE song; at that song's PlaybackNearlyFinished (remaining 0 <= prefetch threshold) TryFetchContinuationBatch fetches the stale Audiobook continuation and appends mid-book chapters after the song. Pre-JF-670 the Audiobook arm was a dead letter that silently self-cleaned, so this cross-playback injection is NEW for books; the music arms (Album/Artist/Playlist) have always had the same lingering-store property, just with same-media-type content.

WHY NO SMALL FIX EXISTS (worker-verified evidence, do not re-litigate without new facts):
1. DeviceQueueManager.SetQueue is called by only the five multi-item play paths (PlayArtistSongs, AlbumPlayService album + playlist, CrossMediaFallback, FollowMe, PlayBook). Single-item plays never call it (ProgressReporter.cs:584 states "a fresh single-song play, which never calls SetQueue").
2. session.NowPlayingQueue is assigned ad hoc at 39 sites across 25 files; no shared funnel.
3. BuildAudioPlayerResponse(PlayBehavior.ReplaceAll) IS the one call every play funnels through (25+ sites), but ReplaceAll does NOT mean "new logical queue": LaunchRequestHandler/ResumeIntentHandler/StartOver/JumpToPosition/SkipForwardBack/ProgressReporter resume-confirm all ReplaceAll the SAME logical queue, and the store entry legitimately keeps serving that queue across a stop (documented linger semantics on QueueContinuation.CachedTracks; the JF-574 resume flow depends on it).
4. All five continuation creators Set their entry BEFORE the launch call, so a Remove inside the response builder would erase the fresh continuation it is launching, and reordering the five Sets after launch breaks long-queue resume.

THE WORK: introduce a queue-identity concept so a continuation is bound to the queue it was minted for and validated at fetch time (fetch only when the current queue still matches the minted identity; mismatch = discard entry), OR an explicit creator-side invalidation lifecycle (every NEW logical queue mint invalidates prior entries, while same-queue ReplaceAll keeps them). Both need a definition of "new logical queue" that survives the resume paths above. This is decision + design work first: the identity key candidates (queue anchor item? mint-time queue fingerprint? explicit generation token on the session?) each have failure modes to write down before choosing.

VERIFICATION: a red-green pin for the injection scenario (stale book continuation + fresh single-song play => no fetch at exhaustion); the music linger semantics stay working (stop album mid-way, resume same album => continuation still serves, JF-574 pin); full suite both TFMs.

OUT OF SCOPE: any change to the per-arm fetchers themselves (JF-666/JF-670 shapes stay); the JF-672 chapter-order and JF-673 fallback-total probes ride Paolo's device round separately.
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
