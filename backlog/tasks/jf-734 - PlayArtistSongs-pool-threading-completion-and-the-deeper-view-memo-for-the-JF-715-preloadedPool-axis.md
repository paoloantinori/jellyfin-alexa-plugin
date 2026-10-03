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
- [ ] #1 PlayArtistSongs' fall-through SearchAsync consumes multiValue.Pool; the JF-702 counting pin updated 2 -> 1 with its comment
- [ ] #2 The per-view scoped-fetch memo designed (thread-safety decision recorded) and either implemented or explicitly re-scoped with a reason
- [ ] #3 dotnet test passes both TFMs
- [ ] #4 /simplify + /code-review high passed
<!-- DOD:END -->
