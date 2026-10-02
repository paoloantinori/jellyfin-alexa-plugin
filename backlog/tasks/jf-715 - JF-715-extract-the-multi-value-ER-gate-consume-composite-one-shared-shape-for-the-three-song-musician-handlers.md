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
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 The gate-consume composite extracted, all three song+musician sites on it
- [ ] #2 IsGenericMusicQuery shares a home with the composite (no cross-handler static)
- [ ] #3 Pool threading decided for SearchAsync's fall-through legs (done or skipped with reason)
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
