---
id: JF-702
title: >-
  JF-702 - adopt the JF-690 multi-value ER arbitration gate on the remaining 5
  musician-slot call sites
status: To Do
assignee: []
created_date: '2026-10-02'
labels:
  - routing
  - ux
dependencies:
  - JF-690
references:
  - >-
    backlog/tasks/jf-690 -
    JF-690-shared-first-word-catalog-synonyms-auto-play-Amazon's-top-ER-rank-no-disambiguation-prompt.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn by the JF-690 worker (hand-created in the worker
worktree per the number reserve; max existing was JF-701), satisfying the
review-recommendation discipline: the JF-690 code-review high gate flagged that
its task file documents unwired adoption sites without a task owning them.

JF-690 wired `MultiValueErDisambiguation.TryArbitrate` into the two
live-evidenced paths only (PlaySongIntentHandler, PlayArtistSongsIntentHandler).
Multi-value ER is a property of the catalog-backed JellyfinArtist slot TYPE, not
of those two intents, so recurrence is CERTAIN the moment a shared first word is
spoken on another musician-slot intent ("metti in coda pink", "gli album di
pink" silently arbitrated by Amazon's ER rank).

Remaining sites (the JF-690 simplify review corrected the original 9-name list;
4 of those handlers carry no musician slot at all):
- Plain one-line adoptions, no competing session state:
  AddToQueueIntentHandler, PlayNextIntentHandler, PlayAlbumIntentHandler,
  QueryArtistLibraryIntentHandler. Each passes its index/libraryManager to the
  gate and returns the ask (and honors the single-resolved artist, the JF-690
  contract).
- FindSongIntentHandler: needs its own analysis BEFORE wiring. The original
  deferral reason was over-broad: a FIRST-turn FindSong request carries no
  FindSongSessionData, so the force-route interplay does not apply there; only
  the elicited/multi-turn legs (AwaitingArtist state) set session data that a
  disambiguation ask would coexist with through HandlerSelector routing. Scope:
  decide per-leg (first-turn wire, multi-turn defer or handle), with a
  HandlerSelector-level test for the coexistence shape.

GATE-MARKER ADDENDUM (2026-10-02, orchestrator review of the JF-690 merge): while
adopting, also close the pool-sharing seam the gate marker flagged: both wired call
sites fetch the artist pool twice on a multi-value leg (once inside TryArbitrate for
resolution, once at the handler's JF-420/JF-652 gates); arbitration.Pool already ships
the gate's fetch, so the adopters should consume it instead of re-fetching (the
PlayArtistSongs/PlaySong sites can take the same fix in this task or a spin-off).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 The 4 plain sites arbitrate multi-value ER through the shared gate
- [ ] #2 FindSong first-turn vs multi-turn decision made and implemented or explicitly re-scoped
- [ ] #3 Per-site handler pins for the ask and single-resolved legs
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
