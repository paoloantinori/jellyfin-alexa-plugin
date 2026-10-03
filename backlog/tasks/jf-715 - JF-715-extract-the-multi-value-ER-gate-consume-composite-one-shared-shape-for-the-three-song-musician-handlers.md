---
id: JF-715
title: >-
  JF-715 - extract the multi-value-ER gate consume composite (one shared shape
  for the three song+musician handlers) and thread the arbitration pool
  through SearchAsync's fall-through legs
status: To Do
assignee: []
created_date: '2026-10-02'
labels:
  - routing
  - dedup
dependencies:
  - JF-702
references:
  - >-
    backlog/tasks/jf-702 -
    JF-702-adopt-the-JF-690-multi-value-ER-arbitration-gate-on-the-remaining-5-musician-slot-call-sites.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn by the JF-702 worker, satisfying the
review-recommendation discipline: the /simplify and /code-review-high rounds on
the JF-702 adoption diff surfaced real structural findings whose fix spans
JF-690's shipped PlaySong site (blast radius beyond the adoption diff), so they
were skipped with reasons and filed here instead.

Finding 1 (reuse/simplification/altitude all converged on it): the ~30-line
"scope-restriction if / TryArbitrate / ask return / survivor adopt /
ids-empty fall-through search" sequence now exists THREE times verbatim:
PlaySongIntentHandler (~304-345, the JF-690 wired original),
AddToQueueIntentHandler (~124-165), PlayNextIntentHandler (~124-163), differing
only in the RetryAsync label string. The diff took the pattern from 1 instance
to 3, which is the extraction trigger the JF-658 fold culture uses. Shape:
one shared composite on MultiValueErDisambiguation or BaseHandler (e.g.
TryArbitrateOrSearchAsync) taking the scope predicate and the dbQuery lambda as
arguments, mirroring the ArtistSearch.SearchAsync policy-axes pattern; the
handlers keep only the adopt-vs-not-found tail. Lighter variant suggested by
the JF-702 code-review high round: a TryArbitrate overload taking the
constraint-slot probe (the songQuery restriction), which both dedups the
wrapper condition and stops the twins reaching into
PlaySongIntentHandler.IsGenericMusicQuery as a cross-handler static.
Related sub-finding: the scope
predicate PlaySongIntentHandler.IsGenericMusicQuery is consumed cross-handler
as a static from both twins; if the composite is extracted, the predicate moves
into it (or to a shared home) and the cross-handler reference disappears.

Finding 2 (efficiency): on the fall-through legs (multi-value ER that closes
with NO survivor: Ask null, ResolvedArtist null, 0 library resolves), the pool
TryArbitrate materialized (Pin + ResolveForUser + GetArtists) is discarded and
the handler's SearchAsync immediately re-materializes the same scoped pool
internally (its tier-1 allArtists). Affected: PlaySong, the twins, PlayAlbum.
PlayArtistSongs is already fixed (JF-702 seeds artistPool from
arbitration.Pool); the remaining sites cannot consume Pool without an optional
pool parameter on ArtistSearch.SearchAsync, a contract change on the ONE shared
chain for the rarest leg (the same reason JF-690's review skipped the PlaySong
half and JF-702 skipped these). Cost: one redundant ResolveForUser + one full
artist-list copy per affected request, rare leg only. If taken, SearchAsync
gains an optional preloaded pool/seeded-view parameter.

Finding 3 (altitude, judgment-level, no defect): the PlayAlbum
arbitrationResolvedArtist flag ORs "proven" into the JF-471/JF-473 re-judgment
gates by hand at each call site; the deeper fix pushes the bypass into
PassesArtistMatchAcceptance (short-circuit when the match name exactly equals
an ER canonical, the JF-420.1 equality precedent), which requires threading the
ER candidates into the shared CrossMedia helper signature used by several
sites. Every future acceptance gate in PlayAlbum must remember the flag until
then (the flag's doc comment carries the contract).

None of the three blocks anything; the adoption diff is green and the pins
hold. Sequence after landing: do finding 1 first (it subsumes the
IsGenericMusicQuery home), findings 2-3 ride the same or a follow-up diff.

AUDIT ADDENDUM (2026-10-02, JF-702 gate-marker round): Finding 2's site list (the
discarded-arbitration-Pool waste on fall-through legs) named PlaySong, the twins, and
PlayAlbum but MISSED the two adopters the JF-702 merge itself added:
QueryArtistLibraryIntentHandler and FindSongIntentHandler's two gated legs (first-turn
keywords-empty and AwaitingArtist musician) carry the identical waste (zero-resolve
multi-value legs materialize the scoped pool in TryArbitrate, then the fall-through
ArtistSearch.SearchAsync re-materializes it internally). When this task lands the
SearchAsync pool-threading fix, cover ALL of: PlaySong, AddToQueue, PlayNext, PlayAlbum,
QueryArtistLibrary, FindSong. ALSO fold the normalization divergence the same review
documented: the twins probe the RAW song slot while PlaySong probes the
carrier-stripped/romanized value (no reachable divergence today, Latin-only table); the
composite's constraint-slot probe overload must OWN the normalization so the three
song-musician sites cannot drift apart on a future generic word whose stripped form
differs.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The gate-consume composite extracted, all three song+musician sites on it
  (MultiValueErDisambiguation.TryArbitrateOrSearchAsync: probe, arbitrate,
  ask-or-adopt-or-search, the artist not-found Terminal, the pool-seeded
  fall-through; PlaySong + both twins call it, each keeping only its
  site-specific tail; the per-site ~30-line sequences are gone. The constraint
  axis rides TryArbitrate's rawConstraintSlot overload, whose probe
  IsGenericSongConstraint OWNS the raw-slot normalization per the addendum:
  carrier-strip + romanize + article-strip membership from the RAW slot, the
  twins converged onto PlaySong's richer probe, pinned by
  AddToQueue_CarrierBleedGenericWord_CompositeNormalization_OpensTheGate with a
  real red proof against the pre-fold raw probe.)
- [x] #2 IsGenericMusicQuery shares a home with the composite (no cross-handler static)
  (moved to MultiValueErDisambiguation with GenericMusicWords, the article
  table, and StripSongCarrierPhrase; the twins' PlaySongIntentHandler static
  references are gone; the simplify round then made IsGenericMusicQuery/
  StripGenericMusicLeadingArticle private with PlaySong's generic-word bypass
  reading the ONE raw-slot entry IsGenericSongConstraint, so the membership and
  normalization levels cannot fork.)
- [x] #3 Pool threading decided for SearchAsync's fall-through legs (done or skipped with reason)
  (DONE at all six addendum sites: SearchAsync gained the optional preloadedPool
  axis consumed on the in-memory branch only, with the pool-covered leg also
  skipping the now-dead scope resolution; PlaySong/the twins thread inside the
  composite, PlayAlbum via arbitrationPool, QueryArtistLibrary via
  multiValue.Pool, FindSong's both legs via the reworked
  ArbitrateMusicianMultiValue returning the full arbitration; per-site counting
  pins assert GetArtistsCalls == 1 (red-proven by sabotaging the PlayAlbum and
  QueryArtistLibrary wirings: exactly those two pins failed), plus the
  ArtistSearch unit pins (pool consumed same-results; DB branch ignores a pool)
  and the CrossMedia kana-tie pin (the hoisted scoped pool serves both the
  chain and the JF-652 near-tie check, one fetch). SKIPPED with reason:
  PlayArtistSongs, outside the enumerated surface though it is the seventh gate
  site with the same double fetch, FILED as JF-734 with both review rounds'
  evidence.)
- [x] #4 dotnet test passes both TFMs
  (5038/5038 net9.0 and 5038/5038 net10.0, the 5019 baseline + 19 pins: 8
  adoption-suite pins (six pool-threading count pins, the carrier-bleed probe
  pin, the not-found Terminal pin folded into AddToQueue's), the kana-tie pool
  pin, 2 ArtistSearch preloadedPool pins, the FindSong warming choke pin, and
  the gate-marker rework's 7 (the trailing-space carrier end-to-end pin, the
  4-row trailing-space/carrier probe theory, the all-carrier-bleed guard pin,
  and the empty-constraint-slot-unrestricted F2 pin). Final-state full suite,
  dotnet test -m:1, once per round.)
- [x] #5 /simplify + /code-review high passed
  (simplify: 4 parallel angle agents, 6 findings applied (orphaned Dispose doc
  comment deleted; dbQuery lambda folded to the retryLabel axis with the
  composite building the RetryHelper channel; the composite pins internally,
  structurally guaranteeing one publish, deleting the 3 per-site pins; the
  probe consolidated to ONE entry with PlaySong's bypass on it; the site
  comments trimmed to site-specific facts; the dead ResolveForUser skipped on
  pool-covered legs; the test mock lambda and the PlaySong intent builder
  deduped into shared helpers) and 5 skipped with recorded reasons (PinIfReady
  fold, result-struct shape, the NoIndex direction pin, the view-memo redesign,
  the constraint-param placement; the first three noted here, the last two
  filed in JF-734). code-review high: 4 findings, 3 applied (the
  choke-preserving unguarded Pin at the composite, FindSong's two legs, and
  QueryArtistLibrary, fixing a REAL JF-715 regression the review caught: the
  readiness-guarded pin nulls a warming index and silently defeats SearchAsync's
  JF-419.2 warming choke at FindSong, the one handler whose entry gate covers
  only the song index; pinned by the new
  FindSong_MusicianLeg_ArtistIndexWarming_ThrowsAtTheChokePoint with a red
  proof against the guarded shape. Plus the composite's debug logs regained
  per-site attribution via retryLabel and the result-count line) and 1 filed
  (PlayArtistSongs threading, JF-734).)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the JF-715 worker. THE COMPOSITE SHAPE: MultiValueErDisambiguation
grew TryArbitrateOrSearchAsync, the one gate-consume shape for the three
song+musician sites (PlaySong, AddToQueue, PlayNext): pin the index once at
composite entry (the structural one-publish guarantee, choke-preserving
unguarded Pin), probe the constraint slot through the owned normalization
(IsGenericSongConstraint: carrier-strip, romanize, article-strip membership,
from the RAW slot), arbitrate via TryArbitrate's new rawConstraintSlot axis,
return the ask / adopt the collapse survivor / run the fall-through artist
search SEEDED with the gate's pool, and speak the artist not-found Terminal;
the handlers keep only their tails (PlaySong's generic-word artist-play
bypass, now probing the same ONE entry; the twins' queue operations). The
site-specific retry label is the composite's one policy axis; the three
~30-line sequences and the cross-handler PlaySongIntentHandler.IsGenericMusicQuery
statics are gone (the whole probe family moved to the composite's home, the
membership level private). PER-SITE PRESERVED RESTRICTIONS: PlayAlbum keeps
its JF-411 musician-only wrapper around the plain (unrestricted) TryArbitrate
plus the arbitrationResolvedArtist flag feeding the JF-471/JF-473 re-judgment
gates; FindSong keeps both its restricted legs (first-turn keywords-empty and
AwaitingArtist musician-supplied, the transcript leg ungated) and its helper
now returns the full arbitration so each leg threads the pool itself;
QueryArtistLibrary keeps the unrestricted PlayArtistSongs shape with the
listing tail; the twins keep the ask-leg queue-shift contract documented at
their composite calls. POOL-THREADING PER-SITE OUTCOMES: all six addendum
sites DONE (PlaySong + the twins inside the composite; PlayAlbum,
QueryArtistLibrary, and FindSong's two legs at their own SearchAsync calls),
each pinned by a GetArtistsCalls == 1 counting test with real red proofs;
CrossMediaFallback's TryEntityFallbackAsync hoists the scoped pool once for
both its consumers (the chain and the JF-652 near-tie check), pinned by the
kana-tie count; PlayArtistSongs NOT threaded (outside the enumerated surface
despite being the seventh gate site with the identical double fetch) and
FILED as JF-734 together with the altitude round's deeper alternative (a
per-view scoped-fetch memo that would make the preloadedPool parameter
structurally unnecessary) and the optional PinIfReady fold. The code-review
high round caught one real regression before it shipped (the guarded pin's
warming-choke defeat at FindSong, whose entry gate covers only the song
index); the fix (unguarded Pin everywhere JF-715 pins) and its choke-point pin
are red-proven. GATE-MARKER REWORK (5 findings): F1 applied as hardening
  with a mechanism correction recorded honestly (today's CarrierPhrase
  structurally refuses empty cuts, strict length compare plus the
  empty-remainder continue, so no reachable slot reclassifies generic to
  non-generic; the empty-normalized-is-generic guard plus the trailing-space
  pins now own the class anyway); F2 applied (IsNullOrWhiteSpace sentinel, an
  empty slot runs the gate unrestricted like null, pinned both ways); F3
  applied (viewId/poolId identity in the pool-consumption log AND the gate's
  zero-resolve fetch log, so a contract breach is diagnosable by comparing the
  pair across one request's lines); F4 applied (CountingArtistIndex captures a
  readiness-frozen view, honest about list/Count staying live for the
  counting); F5 SKIPPED (normalize-once in PlaySong would force a second probe
  entry or a signature change to save sub-microsecond duplicate normalization
  on a DB-query-heavy path). The rework's own focused code-review pass
  (behavioral delta exceeded trivial) returned 5 doc-sync findings: 3 applied
  (the gate-side identity counterpart log, the F4 comment narrowed to what the
  fake actually freezes, the sentinel docs naming whitespace with both guard
  sites cross-referenced), 2 skipped (a shared test-view base for one
  consumer; collapsing the layer-distinct empty-guard comments). Suites
  5038/5038 both TFMs (baseline 5019 + 19 pins), final
state, once; no new compiler warnings. Test-only production surface: six
handlers plus the shared chain and the composite; no deploy (the orchestrator
merges).
<!-- SECTION:FINAL_SUMMARY:END -->
