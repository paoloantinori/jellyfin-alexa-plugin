---
id: JF-974
title: >-
  Album-shuffle as content-scoped play: 'shuffle the album {album}' on
  ShufflePlayIntent + BuildAlbumShufflePlayResponseAsync (the JF-965 twelfth
  row)
status: To Do
assignee: []
created_date: '2026-11-25 18:22'
labels:
  - nlu
  - alignment
  - playback
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The twelfth JF-965 row, split out 2026-11-25: en-US 'shuffle the album {album}' cannot ship as a model row alone because no current handler gives it honest semantics. Evidence and constraints gathered in the JF-965 session:

WHAT EXISTS: ShufflePlayIntent owns playlist shuffle only (ShufflePlayIntentHandler reads the playlist slot, delegates to AlbumPlay.BuildPlaylistPlayResponseAsync with shuffle: true). BuildAlbumPlayResponseAsync (the ordered album path) has NO shuffle parameter and is a resume machine (progressive pages, JF-796/JF-804 deep resume, JF-625 seek-mode concat): shuffle semantics are fresh-play and bypass all of it. AlbumPlay.BuildPlaylistPlayResponseAsync's shuffle arm (JF-927/JF-713) is the proven pattern to mirror: unpaged FULL track list, deduped first-occurrence (queue advance resolves by first-match index; a duplicate id cycles forever), DeriveShuffledQueue into a PendingShuffledQueue snapshot, launch built FROM the snapshot, CommitShuffledQueue after the build, MirrorQueueToSession, QueueContinuationStore.Remove unconditionally, NEVER seed a continuation (the committed shuffled queue is complete; a seeded continuation would append the source-order tail).

DESIGN SHAPE (from the session): a BuildAlbumShufflePlayResponseAsync sibling in AlbumPlayService mirroring the playlist arm: romanize + kana flag at entry, ResolveJellyfinUser, album resolution via the SAME AlbumPlay.BuildAlbumQuery helper PlayAlbum's title path uses (RetryAsync-wrapped) with the SearchItemsFuzzyAsync fallback (kana-barred, the playlist path's shape) and NotFoundAlbum on miss; unpaged scoped track fetch (BuildScopedAlbumTracksQueryUnpaged with the ParentId-then-AlbumIds retry the page path uses); then the derive-commit tail verbatim from the playlist arm. HARD CONSTRAINT: the launch must NOT pass collectionParentId/collectionStartTicks (the JF-625 seek-mode concat builds a source-order whole-album stream; a shuffled album is not a coherent concat timeline), so the shuffle arm always plays per-track AudioPlayer with the queue driving advance. OPEN DECISION for review: multi-match album names (PlayAlbum disambiguates via HandleFuzzyMiss; decide whether the shuffle twin reuses that machinery or ships single-match-only first).

ROUTING: the row lands on ShufflePlayIntent (en-US first; per-locale wording stays out per JF-963) with an album slot matching PlayAlbumIntent's per-locale album slot type (AMAZON.MusicRecording in the 16 free-text locales, AlbumName in it-IT). Cross-locale slot-set divergence needs the validator checked. Handler: album slot non-empty routes to the album path; both empty elicits (the playlist elicit must name the invoking intent per JF-550).

Alternative considered and rejected: putting the row on PlayAlbumIntent (plays ordered; a semantic lie, the exact gap the research names). Considered and deferred: delegating from ShufflePlay to PlayAlbum then post-shuffling the queue (re-introduces the JF-927 window problem on the progressive queue).

DELIVERABLE: template row(s) + slot + handler branch + builder method + unit tests + NLU fixtures, through the full nlu-debug-protocol discipline (graph diff, tokens lint, Gate-5 card, lab round) before any live deploy.
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
