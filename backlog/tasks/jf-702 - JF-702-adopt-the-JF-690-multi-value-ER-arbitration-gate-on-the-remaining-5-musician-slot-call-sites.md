---
id: JF-702
title: >-
  JF-702 - adopt the JF-690 multi-value ER arbitration gate on the remaining 5
  musician-slot call sites
status: Done
assignee: []
created_date: '2026-10-02'
updated_date: '2026-10-02 17:56'
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

GATE-MARKER TAIL (2026-10-02, orchestrator review of commit 42b62cbd, 3 findings; the
review compensated for the worker's permission-denied red proofs with source-level
byte-identity reads, mental sabotage of the load-bearing pins, and real suite runs of the
new class 23/23 and the touched handlers 348/348 on both TFMs): all five scrutiny axes
verified clean (the twins' restriction wrappers are genuine no-ops on excluded shapes,
the gate inside IsGenericMusicQuery with the old body verbatim under artistIds.Count ==
0; PlayAlbum's wrapper after the JF-489 strip; FindSong's keywords-empty wrapper; the
coexistence pin drives the real interceptor and production HandlerSelector.Select
end-to-end; the pool scopes match with Pin idempotent and identical ResolveForUser
inputs; the QueryArtistLibrary listing-to-playback shift deliberate in three places; the
musician-slot census closed at seven gated intents). F2 APPLIED (both real-title pins
strengthened: floyd joins the library so the multi-artist shape is representable, and
the assert became total - every title query rank-#1-only - so an extra unscoped query or
a both-artists scoping FAILS instead of coexisting; 23/23 green after); F3 APPLIED (the
raw-slot vs stripped-value probing divergence documented at both twin sites with the
JF-715 composite named as the normalization's future owner); F1 APPLIED (JF-715's
Finding-2 site list extended with QueryArtistLibrary and FindSong's two gated legs, plus
the normalization fold note).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The 4 plain sites arbitrate multi-value ER through the shared gate
- [x] #2 FindSong first-turn vs multi-turn decision made and implemented or explicitly re-scoped
- [x] #3 Per-site handler pins for the ask and single-resolved legs
- [x] #4 dotnet test passes both TFMs
- [x] #5 /simplify + /code-review high passed
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 42b62cbd + gate-marker tail ea0320ee, merged as ce1d9810. The multi-value ER gate adopted on every remaining musician-slot site: AddToQueue and PlayNext with PlaySong's generic-word scope restriction; PlayAlbum restricted to the musician-only JF-411 shape with the new arbitrationResolvedArtist flag closing a NEW seam (the JF-471/JF-473 re-judgment gates would have refused the proven survivor by scoring against the stale rank-#1 canonical); QueryArtistLibrary in the unrestricted shape with the listing-to-playback shift documented; FindSong wired on both musician-supplied legs (first-turn keywords-empty and AwaitingArtist), replacing the JF-690 deferral with evidence including the HandlerSelector-level coexistence test. Pool-sharing closed on PlayArtistSongs with the counting pin. 23 new pins. Worker gates green (simplify 2 applied incl. the verified-false-positive rejection; code-review high 3 applied, 2 filed as JF-715, 1 verified-no-action); the worker's sabotage red proofs were permission-denied and honestly reported, and the orchestrator gate-marker compensated: source-level byte-identity reads of all three restriction wrappers, the end-to-end coexistence pin verified driving the real interceptor and production selector, pool-scope matching, mental sabotage confirming the load-bearing pins, and real suite runs (23/23, 348/348 both TFMs). Gate-marker findings all applied in the tail (the two real-title pins strengthened to total rank-#1-only assertions over a two-artist library; the raw-vs-stripped probing divergence documented at both twin sites; JF-715's pool-waste site list completed with the two new adopters). Suites: worker and orchestrator independent 4956/4956 both TFMs, merged-tree 4958/4958 both TFMs exit 0. Production surface changed (five handlers): deployed in the post-merge deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
