---
id: JF-974
title: >-
  Album-shuffle as content-scoped play: 'shuffle the album {album}' on
  ShufflePlayIntent + BuildAlbumShufflePlayResponseAsync (the JF-965 twelfth
  row)
status: In Progress
assignee: []
created_date: '2026-11-25 18:22'
updated_date: '2026-11-26 19:48'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
GATE-5 CARD (orchestrator, written 2026-11-26 BEFORE implementation and any lab round):

Question: does the 'shuffle the album {album}' row land on ShufflePlayIntent with honest content-scoped semantics, and does its en wrapper twin ('to shuffle the album {album}') survive arbitration?

H1 (row + twin route): lab round routes 'shuffle the album {album}' and its bare twin 'to shuffle the album {album}' to ShufflePlayIntent with the album slot filled; no sibling intent steals either (PlayAlbum/PlayByGenre/PlaySong are the steal candidates).

H2 (twin loses arbitration): the base row routes but the twin is stolen (like JF-965's 'to start the record'); then the twin is DROPPED, documented here, and only the base row ships.

H3 (base row stolen): the base row itself loses arbitration; the whole routing choice is re-examined before any deploy.

Arms: control = current en-US model battery (before the row); treatment = same + the new row(s). Sentinels: PlayAlbum carriers ('play the album {album}'), PlayByGenre ('listen to {genre}'), existing ShufflePlay playlist rows.

Decision map: H1 -> ship both rows, fixtures pin BARE TAILS only; H2 -> ship base only + document; H3 -> no ship, revisit. The lab round runs on the LAB skill (sample-content hypothesis); NO live deploy without the maintainer's explicit go (the JF-965 rule).

OPEN DECISION RESOLVED by orchestrator: the multi-match album shape REUSES PlayAlbum's disambiguation machinery (HandleFuzzyMiss path) rather than shipping single-match-only; consistency over a silent not-found for ambiguous names, and the machinery is shared.

PROTOCOL SPLIT: the worker implements code/templates/fixtures/static validation only; the lab round, wrapper-twin probes, and any live verification are the orchestrator's (this card governs them).
<!-- SECTION:NOTES:END -->
