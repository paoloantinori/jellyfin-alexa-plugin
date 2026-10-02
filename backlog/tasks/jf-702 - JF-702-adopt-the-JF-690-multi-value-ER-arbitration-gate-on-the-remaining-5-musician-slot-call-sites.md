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
Closed by the JF-702 worker (worktree agent-a039cdd1e14113f82, base 7592ccaf). All five
musician-slot sites now drive MultiValueErDisambiguation.TryArbitrate; 23 new pins in
MusicianMultiValueErAdoptionTests; suites 4956/4956 net9.0 and 4956/4956 net10.0
(baseline 4933 + 23), 0 new compiler warnings.

Per-site decisions:
- AddToQueue + PlayNext (the twins): the gate carries PlaySong's scope restriction
  (PlaySongIntentHandler.IsGenericMusicQuery on the song slot), the same
  song+musician constraint logic JF-690's code-review found for PlaySong: a REAL song
  title keeps today's rank-#1 scoped title search because the confirm leg plays the
  artist and can neither preserve the requested song nor the queue/insert-next
  operation; a generic music word ("metti in coda la musica di pink") lets the gate
  ask, and a stale-catalog collapse scopes the song search to the proven survivor
  (the twins have no PlaySong-style play-the-artist bypass; the generic-word title
  miss on the collapse leg is pre-existing either way, documented at the site).
- PlayAlbum: the gate is restricted to the musician-only shape (album slot empty
  after the JF-489 retry block, the JF-411 album-by-artist resolution the JF-690
  filing named); an album TITLE in hand keeps the rank-#1 scoped search. NEW seam
  found and closed: the JF-471/JF-473 re-judgment gates score the match against
  musicianSearch (the stale rank-#1 canonical on a collapse leg), so the survivor
  ("ABBA") would be refused against "P!nk" despite being proven; the
  arbitrationResolvedArtist flag skips both re-judgment gates for the survivor
  (the gates exist to judge SEARCH results; proven evidence outranks the stale
  canonical, the same principle JF-690 applied at PlaySong/PlayArtistSongs). Pin:
  the collapse plays the survivor's album (red-loads without the flag by
  construction: the acceptance gate returns NotFoundAlbumByArtist).
- QueryArtistLibrary: PlayArtistSongs' shape, unrestricted (the musician slot is
  the intent's only content input). Documented contract shift on the ask leg: the
  confirm leg plays the artist instead of listing, the accepted JF-690 adoption
  trade.
- FindSong: WIRED, both first-turn and the AwaitingArtist musician-supplied leg,
  with evidence replacing the JF-690 deferral: (1) a first-turn request carries no
  FindSongSessionData, so the feared force-route interplay cannot occur (the ask is
  the FIRST session state written and the FindSong flow never opens); (2) on the
  multi-turn leg the interplay is ALREADY owned by the JF-398 machinery the JF-690
  notes predate the sharpening of: AskMultipleArtists marks every other flow's keys
  for removal, so the interceptor strips FindSongSessionData from the ask and the
  confirm/cycle turns route to Yes/NoIntentHandler through the normal CanHandle
  order. The coexistence pin drives the full chain: handler ask -> interceptor merge
  over an open FindSong session (FindSongSessionData gone, disambig_* present) ->
  HandlerSelector routes the yes to YesIntentHandler (control: with FindSong state
  kept, the yes force-routes to FindSongIntentHandler). NOT wired by decision: the
  AwaitingKeywords musician-as-keywords fallback and the Disambiguating pick leg
  (the musician slot there is a keyword/pick surface, not an artist-search driver;
  gating it would change semantics, not adopt the gate).

Pool-sharing closure (the addendum): PlayArtistSongs seeds its artistPool cache
from arbitration.Pool, so on a multi-value leg the JF-420/JF-652 gates consume the
gate's fetch instead of a third materialization; closed legs are null (byte-
identical). Pin: the counting index sees exactly TWO GetArtists calls on the
multi-value fall-through-then-JF-420-gate leg (the gate's + SearchAsync's tier 1).
PlaySong's fall-through re-fetch lives INSIDE the shared SearchAsync chain (its
tier-1 materialization feeds tiers 2-4); no handler gates follow the arbitration
there, so consuming Pool would need a contract change on the ONE shared chain for
the rarest leg - documented at the addendum level as deliberately not taken.

Gates: /simplify (4 angles, findings applied/skipped in the commit message) and
/code-review high (6 findings: 3 applied, 2 filed to JF-715, 1 verified-no-action)
run on the final diff; the applied set: the FindSong first-turn gate restricted to
the keywords-empty shape (a titleKeywords value in the SAME utterance is in-hand
content the confirm leg cannot preserve; the both-slots shape keeps today's
rank-#1 resolution byte-identically, new pin), the twins' comments now name the
ask leg's accepted operation shift and the collapse not-found naming the survivor,
the FindSong in-file arbitration helper (one collapse log literal, the simplify
fold), and the PlayAlbum closed-gate-leg clarifying comment. Filed to JF-715: the
three-way gate-consume composite extraction (PlaySong + the twins, with the
TryArbitrate constraint-slot-probe overload variant) and the pool threading
through SearchAsync's fall-through legs. Verified-no-action: the QueryArtistLibrary
ordering finding (the canonicalMusician gate already made the JF-440 fallback
unreachable for ER-resolved slots before the adoption; behavior unchanged).
Caveat honestly recorded: the sabotage-style red proofs (temporarily disabling
each new mechanism to watch its pin flip) were attempted and DENIED by the
session's permission layer (source-edit tampering classifier); the pins'
load-bearing quality is argued structurally instead: the pool pin counts
GetArtists (removing the seed provably yields 3 calls), the survivor pins assert
the ABSENCE of any MusicArtist DB query (only the collapse supplies the scope),
and the PlayAlbum flag pin fails as NotFoundAlbumByArtist without the skip.

Do not merge from the worker branch; orchestrator merges via review.
<!-- SECTION:FINAL_SUMMARY:END -->
