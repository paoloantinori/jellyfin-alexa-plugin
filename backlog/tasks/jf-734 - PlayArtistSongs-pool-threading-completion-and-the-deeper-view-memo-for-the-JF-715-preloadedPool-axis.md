---
id: JF-734
title: >-
  JF-734 - PlayArtistSongs pool-threading completion (the seventh gate site)
  and the deeper alternative to the preloadedPool axis: a per-view scoped-fetch
  memo
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - routing
  - dedup
  - efficiency
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn by the JF-715 worker (reserved number), satisfying
the review-recommendation discipline: the JF-715 simplify round (reuse angle)
and the code-review high round BOTH flagged the same site, and the altitude
round recorded a deeper alternative to the mechanism JF-715 shipped. All three
findings are real and outside JF-715's enumerated surface (the task's addendum
mandated exactly six threading sites and declared PlayArtistSongs "already
fixed" per the JF-702 reading), so they are filed here rather than applied.

FINDING 1 (the one-line completion, both rounds): PlayArtistSongsIntentHandler
is the seventh multi-value-ER gate site and the one whose JF-702 history
motivated the pool-sharing mechanism, but its fall-through
`ArtistSearch.SearchAsync` call (~line 262) still re-materializes the scoped
pool the gate already fetched on the zero-resolve leg (TryArbitrate's
`pinned.GetArtists(ResolveForUser)` then SearchAsync's own tier-1 fetch over
the same pinned view and the same scope): one redundant ResolveForUser plus a
full scoped artist-list copy per affected request, the exact waste class the
JF-715 `preloadedPool` axis removes at PlaySong, AddToQueue, PlayNext,
PlayAlbum, QueryArtistLibrary, and FindSong's two legs. The JF-702 pool-sharing
fix covered only the POST-search JF-420/JF-652 gates (`multiValue.Pool` seeds
the handler's `artistPool` cache at ~line 287); the search itself was never
threaded. FIX: `preloadedPool: multiValue.Pool` on the line-262 call (the
parameter's contract already holds there: `pinnedIndex` is passed to both
TryArbitrate at ~236 and SearchAsync at ~263; the mode/asr/parallelDbTiers
policy axes are orthogonal), then update the JF-702 pin
(`MusicianMultiValueErAdoptionTests.PlayArtistSongs_MultiValueEr_Jf420Gate_ReusesTheGatePoolFetch_NoSecondMaterialization`,
asserting `GetArtistsCalls == 2`) to expect 1, with the JF-702 comment's "TWO
GetArtists calls (the gate's and SearchAsync's tier-1)" sentence updated to
match.

FINDING 2 (the deeper mechanism, altitude round): the `preloadedPool`
parameter is a contract-carrying bypass channel whose safety lives in prose
("must be the same view's scoped fetch"); a pool from a different publish or
scope compiles and runs, silently reintroducing the exact mixed-publish class
JF-448 was filed to kill, and every future gate-to-chain site must remember to
thread it (Finding 1 is the first miss). The root cause one level down: a
pinned view's `GetArtists(topParentIds)` re-runs the scope filter per call
(`DebouncedLibraryIndexService.FilterByLibraryScope`, a full scoped copy), so
any request with two consumers of the same view's scoped list pays twice. The
deeper fix is a per-view memo of the scoped fetch inside the pinned
SnapshotView (keyed per view, hence per-request and per-publish by
construction; the unrestricted case is already the shared immutable snapshot
array, the zero-copy branch, so aliasing is safe under the JF-448 review-F5
immutability contract). With the memo, the gate's fetch primes it,
SearchAsync's internal fetch hits it, and the parameter, the six-site
threading, and the CrossMediaFallback hoist all disappear; a wrong-publish
pool becomes structurally impossible. DESIGN NOTE: the memo adds state to a
hot read object; thread-safety must be decided (views are per-chain in
practice but nothing enforces single-threaded reads), which is why this is a
task and not a drive-by. JF-715 shipped the parameter shape because its task
text mandated it ("SearchAsync gains an optional preloaded pool/seeded-view
parameter") and forbade reordering the accepted JF-448/JF-658 resolution.

FINDING 3 (optional cosmetic fold, simplify/reuse rounds): the
readiness-guarded pin idiom (`index?.IsReady == true ? index.Pin() : null`)
lives at PlayArtistSongs (safe there: the handler entry-gates the artist
index) and inside MultiValueErDisambiguation.TryArbitrate (needs the
ready-only semantics for the gate itself). JF-715 collapsed its own sites onto
either the composite's internal pin or the choke-preserving unguarded
`_artistIndex.Pin()` (PlayAlbum's precedent; a not-ready pinned view keeps the
JF-419.2 EnsureReady choke point throwing, where a null conversion would
silently route to the cold DB chain). If more guarded-pin sites appear, hoist
ONE named decision (`PinIfReady`) beside the existing null-tolerant `Pin`
extension in IArtistIndex.cs so the readiness policy has a single definition;
do NOT mix the two shapes at one call site (the JF-715 code-review finding 1
documents the choke-defeating trap of the guarded form below a handler that
does not entry-gate).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 PlayArtistSongs' fall-through SearchAsync consumes multiValue.Pool; the JF-702 counting pin updated 2 -> 1 with its comment (the call at PlayArtistSongsIntentHandler ~line 268 gains `preloadedPool: multiValue.Pool` beside its existing mode/asr/parallel axes; the contract holds by construction: the handler passes the SAME `pinnedIndex` local to TryArbitrate (~line 235) and to SearchAsync, Pin is idempotent, and both scope resolutions go through the cached ResolveForUser for the same user, so the pool is exactly this view's scoped fetch. Null on every closed-gate leg keeps the internal fetch. The pin `PlayArtistSongs_MultiValueEr_Jf420Gate_ReusesTheGatePoolFetch_NoSecondMaterialization` now asserts GetArtistsCalls == 1 with the comment rewritten to the ONE-call story (gate fetch only; the search and the JF-420 gate both consume it). RED-PROVEN LIVE, both TFMs, sabotage permitted this round: threading replaced with `preloadedPool: null` -> the pin fails on net9.0 and net10.0 ("Assert.Equal() Failure: Values differ", 1 vs 2), reverted, re-run green. The seam comment above `topParentIds` was WIDENED per the code-review high round: the honest post-JF-734 envelope is that ANY tier accept can auto-play a just-removed library's artist unprompted from the gate-time scope, where pre-JF-734 the search re-resolved the scope itself and stale exposure was limited to the prompt shapes plus the JF-420 auto-select)
- [x] #2 The per-view scoped-fetch memo designed (thread-safety decision recorded) and either implemented or explicitly re-scoped with a reason (DECLINED, re-scoped with reason, verdict recorded in the Final Summary below: after the seventh site's threading NO known request path materializes the same view+scope twice, so the memo's present-day payoff is zero; the unrestricted path is already zero-copy (FilterByLibraryScope returns the frozen snapshot array, so the memo would alias a free reference) and single-consumer requests (the overwhelming majority) would pay the bookkeeping for no reuse; the costs are mutable state on the STATELESS SnapshotView hot read object, forcing the thread-safety decision the filing deferred (nothing enforces single-threaded reads; a lock or Interlocked-with-benign-duplicates is the minimum honest shape) plus a VALUE-EQUALITY key over Guid[] (reference equality silently misses; the same-instance flow through LibraryFilter's _topParentCache is an implementation detail, not a contract); the structural insurance (wrong-publish pool impossible) is already contained by the single-producer shape (TryArbitrate is the only pool source, every consumer passes the same pinned instance it handed the gate) and the JF-715 identity-pair consumption log)
- [x] #3 dotnet test passes both TFMs (full suite ONCE on the final state, -m:1: 5093/5093 net9.0 and 5093/5093 net10.0, baseline 5093, no new tests, the pin tightened in place; filtered gates along the way: MusicianMultiValueErAdoptionTests 37/37 both TFMs after every edit round, the four-class affected battery (PlayArtistSongsIntentHandlerTests + PlayArtistSongsSearchModeAxisTests + MusicianMultiValueErDisambiguationTests + ArtistSearchTests) 112/112 both TFMs; Release -warnaserror clean, 0 warnings 0 errors)
- [x] #4 /simplify + /code-review high passed (simplify 4-agent round: reuse CLEAN (the seventh site matches the six-sibling idiom verbatim, no wrapper exists to call); efficiency CLEAN (independently confirmed the O(whole-library) copy removed and that the post-search ResolveForUser stays needed: line ~537 feeds the artist-songs query scope on every play tail); altitude CLEAN (four-axis verification that TryArbitrateOrSearchAsync structurally cannot host this site without becoming a god-shape; grep-verified no eighth unthreaded TryArbitrate site remains); simplification 2 findings + 1 nit ALL APPLIED (the 9-line site comment trimmed to 6 lines dropping the sentences duplicating the SearchAsync param doc, the JF-702 comment, and the seam edit below; the test comment's pre-JF-702 archaeology clause dropped; the section banner now names JF-734; the seam rewrap). code-review high: 3 findings, 2 APPLIED (F1 the seam-comment honesty fix described in #1; F2 repo CLAUDE.md's "three POLICY axes" sentence gained the preloadedPool CONTRACT axis with its same-view contract and the seven-site family, stale since JF-715), 1 FILED as JF-742 (F3 the arbitration+pool-threaded-search sequence now at five hand-synced sites; the fold spans QueryArtistLibrary/FindSong outside this task's enumerated surface, and both reviews' counterweights are recorded in the filing). Nothing else survived: the reviewer independently re-ran the class (37/37) and the full suite (5093/5093 both TFMs), and verified the DB branch never sees a pool (pool non-null implies a ready view))
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Filed-2026-10-04 worker completion. The completion shape: the seventh pool site closed the JF-715 family the way the other six did, one named argument (`preloadedPool: multiValue.Pool`) on PlayArtistSongsIntentHandler's fall-through ArtistSearch.SearchAsync call, the ONLY leg that double-fetched (the zero-resolve multi-value-ER leg; the ask leg returns, the collapse leg skips the search, closed gates ship a null pool and keep the internal fetch byte-identical). The parameter's same-view contract holds by construction at this site (same pinnedIndex local to gate and chain, idempotent Pin, cached same-user scope), documented at the call site. The JF-702 counting pin tightened in place 2 -> 1 (the gate's fetch is the only GetArtists the leg pays) and RED-PROVEN live on both TFMs against the exact sabotage (threading nulled -> pin fails 1 vs 2), the first round the sabotage was permitted after two permission-blocked precedents (JF-702/JF-711). MEMO VERDICT (Finding 2): DECLINED, re-scoped with the reason in DoD #2; the completion leaves the memo zero present-day payoff while its costs (mutable state on the stateless SnapshotView, a forced thread-safety decision, value-equality Guid[] keying) all land on the hottest read object. FOLD VERDICT (Finding 3, PinIfReady): SKIPPED; the census remains exactly two guarded-pin sites (this handler's entry, TryArbitrate), the fold's own trigger ("if more sites appear") is not met, and a named PinIfReady extension would be MORE tempting than the inline ternary given the JF-715-documented choke-defeating trap of the guarded form below a non-entry-gating handler. The code-review high round's third finding (five hand-synced arbitration+threaded-search sites) filed as JF-742 with both reviews' counterweights. The seam above topParentIds was honestly WIDENED per that review: post-JF-734 a stale scope can auto-play unprompted through any tier accept, one leg wider than the JF-702 prompt-only exposure. Gates: simplify 4 agents (3 clean angles, simplification's 2+1 applied), code-review high (2 applied, 1 filed). Suites: 5093/5093 both TFMs once on the final state; Release -warnaserror clean. No deploy (the orchestrator batched post-closure deploy owns it).
<!-- SECTION:FINAL_SUMMARY:END -->
