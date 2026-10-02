---
id: JF-690
title: >-
  JF-690 - shared-first-word catalog synonyms auto-play Amazon's top ER rank with
  no disambiguation prompt
status: To Do
assignee: []
created_date: '2026-09-30 20:40'
labels:
  - catalog
  - routing
  - ux
dependencies:
  - JF-684
references:
  - >-
    backlog/tasks/jf-684 -
    JF-684-catalog-musician-slot-blocks-intent-selection-for-non-catalog-values-bare-artist-names-produce-NO-intent-fuzzy-tiers-voice-unreachable.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn by the JF-684 worker from the /code-review high gate
(hand-created in the worker worktree per the number reserve; max existing was JF-689).

JF-684 added bare first-word synonyms to ARTIST catalog entries, which restores intent
selection for partial names. Live spike evidence (2026-09-30, it-IT, spike version
1197): "suona la musica di pink" returns a MULTI-VALUE ER match, values [P!nk, Pink
Floyd] (both library artists carry a "pink"-matching form), with slotValue.value
already set to Amazon's rank #1 ("P!nk").

The residual behavioral gap, verified in code during the JF-684 review:
- `SlotValueHelper.GetCanonicalValue` reads only `authority.Values[0].Value.Name`
  (SlotValueHelper.cs, the ER_SUCCESS_MATCH branch).
- `PlayArtistSongsIntentHandler` feeds the canonical to the artist search verbatim
  (`musicianQuery = canonicalMusician ?? musician`, the JF-659 contract).
- The resulting exact-name hit auto-plays through the JF-420.1 exact-equality bypass.

So a user saying "pink" in a library holding both P!nk and Pink Floyd gets whichever
Amazon ranks first, silently. The JF-420.2 yes/no disambiguation prompt exists for
exactly this shape but only fires on the raw-text path (no canonical). Net behavior is
still a strict improvement over JF-684's pre-fix state (silence, no request at all),
which is why this was filed rather than blocking JF-684.

Evaluation directions (need a decision + spike):
- A multi-value read alongside GetCanonicalValue (e.g. GetCanonicalValues returning all
  matched names): when ER returns >1 value, drive the existing DisambiguateMultipleArtists
  flow instead of the verbatim canonical feed. Beware: ER multi-value matches also occur
  for ordinary synonym drift where one artist matches via two synonyms (check whether
  Amazon de-dupes per VALUE before prompting; the spike's [P!nk, Pink Floyd] was genuinely
  two distinct values).
- Or accept Amazon's ranking as the tiebreak (documented, zero work) if a device round
  shows the top rank is almost always the intended artist.

Related: JF-688 (post-sync live verification; add a shared-word probe to that round).

## Findings (2026-10-02, worker session, worktree agent-a94e89dbc21d631aa)

### Design note: the arbitration decision (made before implementation)

Live evidence refined since filing (JF-684 post-sync verification, 2026-10-01 06:49,
catalog v1199, live on production): "suona la musica di pink" selects **PlaySongIntent**
(song="la musica" reaching the JF-697 generic-word gate, musician ER_SUCCESS [P!nk, Pink
Floyd]), and "suona la cantante pink" selects **PlayArtistSongsIntent** (ER_SUCCESS,
same catalog values). So the gap is reachable on BOTH musician-slot paths, and the
PlaySong one is the live-evidenced primary.

**Chosen: candidate (a), with library verification before prompting.**
A new `SlotValueHelper.GetCanonicalValues` read (slot + IntentRequest overloads,
mirroring `GetCanonicalValue`) exposes ALL distinct canonical names the first
ER_SUCCESS_MATCH authority returned. When the list carries MORE THAN ONE distinct
name, each candidate is resolved against the user's library by EXACT-name equality
(case-insensitive, trimmed: the JF-420.1 `IsExactNameMatch` normalization, moved to
`ArtistSearch` as the single shared definition) over the pinned in-memory artist
index. When TWO OR MORE DISTINCT library artists resolve, the handler fires the
EXISTING `DisambiguationHelper.AskMultipleArtists` prompt (JF-420.2 yes/no cycling,
winner first in Amazon's rank order so "yes" reproduces today's pick and "no"
explores the alternative). Everything else (single ER value, <=1 resolved artist,
or no ready in-memory index) is byte-identical to today's path.

Why the library-verification step is load-bearing (not prompt-immediately): the
yes leg resolves `disambig_matches[i].Id` as a GUID via `GetItemById`
(YesIntentHandler), so candidates MUST carry real library ids; ER names carry none.
Verification also filters catalog staleness (an ER candidate whose artist left the
library never enters the prompt; weekly sync lag cannot offer a dead end).

Why the ids order is Amazon's rank order: `AskMultipleArtists`' contract is "the
first match is the one yes plays"; keeping Values order makes "yes" pick the SAME
ARTIST today's auto-play picked (Amazon's rank #1), so the prompt adds a choice
on top of the old outcome instead of replacing it. (Code-review correction: the
confirm leg is NOT byte-identical to the direct play; this change aligns its
song ORDERING with the direct paths, see the review outcomes below, while the
confirm queue deliberately remains the artist's full catalog.)

**Alternatives weighed and rejected:**
- (b) Values[0] + log: zero UX change; the driving case (P!nk AND Pink Floyd both
  real artists; the user cannot say which) stays a silent Amazon-rank pick. The
  JF-420 family's premise is that string-indistinguishable ambiguity gets a prompt
  (JF-377's "the prompt is the only no-regression design"); a shared-word
  multi-value ER match is exactly that shape. Its useful core (observability) is
  folded in anyway: the gate logs the multi-value shape on every leg (prompt,
  single-resolve collapse, cold-path skip).
- (c) feed all values to the artist search and let the JF-420 comparison
  arbitrate: mechanically broken. `SearchAsync` takes ONE query; a joined query is
  a fabricated string nobody spoke. Even per-candidate, each canonical name
  EXACTLY equals its own artist's name, so the JF-420 containment-vs-alternative
  comparison never fires (it requires a multi-word query CONTAINING the artist
  name; the JF-420.1 equality bypass exists precisely because exact matches
  auto-play). (c)'s useful core (resolve every candidate, arbitrate at the
  handler) is exactly what (a) implements.
- Prompting with the raw ER names only: dead on the confirm leg (see
  library-verification above).

**Amazon-side dedup question (from the filing), answered:** the ER `values` array
lists DISTINCT catalog values ordered by likelihood; one artist matching via two
synonyms yields ONE value, so a >1 list is genuinely multiple candidate values
(the live spike's [P!nk, Pink Floyd] are two distinct values). The read still
de-dupes defensively by trimmed case-insensitive name (a pathological same-name
catalog pair must not manufacture a two-entry prompt of one artist).

**Wiring scope:** both live-evidenced paths (PlaySongIntentHandler musician
branch, before its artist search; PlayArtistSongsIntentHandler, after its
JF-448 pin and before its `ArtistSearch.SearchAsync` call, reusing the pinned
view) through ONE shared gate (`MultiValueErDisambiguation.TryAskArtist`) so
there is no second copy. The gate closes without the in-memory index (cold/DB
path), the same accepted trade-off class as the JF-420 alternative pool and the
JF-652 near-tie pool (both skip on the database path).

**Residual adoption sites (corrected by the JF-690 simplify review; the original
filing's 9-name list was wrong):** multi-value ER is a property of the
catalog-backed JellyfinArtist slot TYPE, not of the two evidenced intents, so
recurrence is CERTAIN the moment a shared word is spoken on another
musician-slot intent ("metti in coda pink", "gli album di pink"). The real
musician-slot call sites still unwired are 5, not 9 (PlayByGenre, PlayByDecade,
BrowseLibrary, and PlayRandom carry no musician slot at all; verified by slot
grep): AddToQueueIntentHandler, PlayNextIntentHandler,
PlayAlbumIntentHandler, QueryArtistLibraryIntentHandler (4 plain one-line
adoptions: each passes its index/libraryManager to the same gate and returns
the ask; no competing session state), and FindSongIntentHandler (deferred
separately: FindSongSessionData drives next-turn routing through
HandlerSelector, so a disambiguation Ask there sets disambig_* attributes
ALONGSIDE FindSong's own session data and the routing interplay needs its own
tests before wiring). The two wired paths are the live-evidenced ones; the
boundary is evidence-per-path (the repo's wiring culture), not a claim that the
others are immune.

**Catalog-side alternative, rejected:** the only "simpler" root fix would be
PartialNameSynonyms NOT emitting a bare-first-word synonym when two artists
share the word (no shared words, no multi-value ER). Rejected because it
re-introduces the JF-684 bug for BOTH artists on that word (bare/partial names
select no intent at all, the pre-fix silence): the ambiguity is intrinsic to
two real artists sharing a spoken word, so serve-time arbitration is the only
depth that resolves it without losing recall.

**Locale strings: NONE added.** `AskMultipleArtists` speaks
`DisambiguateMultipleArtists` + `DisambiguateReprompt`, both already in all 17
locales ("Ho trovato più artisti: {0}. Vuoi il primo? Di' no per il successivo."),
which already speak the yes/no cycling the state machine implements. DoD #8 N/A.

**Yes leg verification (no code change needed):** the prompt stores the identical
MatchInfo shape the JF-420.2/JF-652 gates store today (real ids, type=artist);
`YesIntentHandler` confirms via `Guid.TryParse` + `GetItemById` + `PlayArtist`
(pinned by the existing JF-667 artist-confirm test, YesIntentHandlerTests.cs:586),
and `NoIntentHandler` advances via `AskNextMatch`. All unchanged machinery.

### Gate outcomes (2026-10-02)

**Simplify (4 agents: reuse / simplification / efficiency / altitude).** Applied:
the two near-verbatim wiring comments trimmed to site-specific facts with the
contract pointer; `ResolveExactNameMatches` inner loop folded to `FirstOrDefault`;
the always-"musician" slotName parameter dropped (the gate owns the slot const,
later `IntentNames.Slots.Musician` after the review round); 6 unused usings
removed from the new test file; a doubled word in the gate doc; the shared
`TestHelpers.AssertSessionOpen` assertion; PlayArtistSongs now passes its pinned
JF-448 view into the gate (no second pin); the residual adoption list corrected
(5 real musician-slot sites, not 9; FindSong's deferral reason sharpened); the
catalog-side alternative (never emit shared bare-first-word synonyms) documented
as rejected. Skipped with reasons: the resolver id-dedup stays (defense-in-depth
behind the name-dedup, both layers independently red-pinned; the doc now states
the layering as chosen); the authority walk unification (the byte-identical
discipline rejects normalizing the Values[0]-invalid edge; GetCanonicalValue is
pre-existing); the cold-path test mock bucket overlap (explicit exclusions avoid
depending on Moq setup-order semantics); promoting the ask assertion into
TestHelpers (per-file assertion helpers are the suite convention; the weaker
existing copy would remain a second copy anyway); hoisting Trim out of the pool
loop (keeps IsExactNameMatch the single normalization owner).

**Code-review high (9 findings: 7 applied, 1 filed, 1 skipped).** Applied:
(1) the PlaySong gate is now restricted to the generic-music-word shape, so a
REAL song title plus a multi-value musician keeps today's rank-#1 scoped title
search (the confirm leg cannot preserve the song constraint; asking would drop
the requested song) - new pin; (2) `YesIntentHandler.PlayArtist` now orders by
`CrossMediaFallback.PopularitySort` like every direct play path (the confirm leg
started an arbitrary DB-order track; no Limit, the confirm queue deliberately
stays the full catalog) - JF-667 test extended; (3) the plain ask builders in
`DisambiguationHelper` (AskFirstMatch x2, AskNextMatch, AskMultipleArtists) now
mark ONLY the keys they write, so a stale unanswered cross-media offer's
`crossmedia_notfound_*` keys no longer survive onto a later plain artist ask
(previously "no" answered the OLD song/album not-found instead of cycling; the
leak was generic to the JF-420.2/JF-652 asks too) - new pin; (4) the gate now
RETURNS the single library artist an ambiguity collapses to instead of letting
the stale rank-#1 canonical drive a not-found with the survivor sitting resolved
(the TryArbitrate struct; both handlers play the proven survivor) - pins
rewritten/added on both paths; (5) `IsExactNameMatch` is null-safe (the resolver
walks every published pool entry; red proof: unguarded it throws
NullReferenceException on a null-named entry); (6) the deferral produced JF-702
same-turn (5 adoption sites, FindSong first-turn vs multi-turn scoping); (7) the
gate's slot literal replaced with `IntentNames.Slots.Musician`. Skipped with
reason: (8) seeding the handler's artistPool cache from the gate's pool fetch
(`arbitration.Pool` is returned for exactly that, but wiring it into
PlayArtistSongs requires reordering the accepted JF-448/JF-658 pool-resolution
structure to save one scoped-pool materialization on the rarest collapse leg;
the field stays available for the JF-702 adopters); (9) [superseded within the
same review] the GetCanonicalValues single-value fast path that the efficiency
round had added was REMOVED again per the review's divergence finding (two code
paths for one read contract; the saved cost is two gen0 allocations).

### Red proofs (all run and read)

1. Gate never fires (Count<=99): exactly the two ask pins flip on both TFMs.
2. Gate over-fires (Count<1 AND resolved<1): exactly the four no-ask controls
   flip on both TFMs.
3. Both dedups removed: exactly the two dedup pins flip on both TFMs (the
   net10.0 first run used --no-build and read stale green; re-run WITH build
   flipped, the documented --no-build trap caught in the act).
4. Rework legs combined (inverted generic-word restriction, single-resolved
   return disabled, crossmedia marking reverted): 5 pins flip on net9.0,
   including the two PlaySong scope pins and the crossmedia pin.
5. Null guard removed: the blank-named-pool pin fails with
   NullReferenceException (an earlier attempt at this sabotage wrote the
   unmodified buffer by mistake and proved nothing; re-done properly).

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-02 in worktree agent-a94e89dbc21d631aa (single commit on
worktree-agent-a94e89dbc21d631aa, base 62185458; not pushed). JF-690: when a
spoken word matches SEVERAL catalog entries (the JF-684 shared-first-word
synonyms, live 2026-10-01: "pink" -> ER [P!nk, Pink Floyd], both real library
artists), the plain JF-659 canonical read kept only Amazon's rank #1 and the
exact-name hit auto-played silently through the JF-420.1 equality bypass. THE
ARBITRATION (candidate (a) with library verification, chosen over (b)
log-only and (c) feed-the-search, both rejected with reasons above): a new
`SlotValueHelper.GetCanonicalValues` read exposes all distinct ER canonical
names; the ONE shared gate `MultiValueErDisambiguation.TryArbitrate`
(Alexa/Handler/) resolves every candidate by exact-name equality
(`ArtistSearch.IsExactNameMatch`, moved to ArtistSearch as the single shared
definition, now null-safe; `ArtistSearch.ResolveExactNameMatches`, rank order
preserved) against the pinned in-memory index, and (i) on >=2 distinct library
artists fires the EXISTING `DisambiguationHelper.AskMultipleArtists` JF-420.2
yes/no prompt with real library ids ("yes" keeps the rank-#1 artist, "no"
cycles), (ii) on exactly 1 resolved artist PLAYS that proven survivor (stale
catalog candidates never drive a not-found), (iii) on every closed-gate leg
(single value, no ready index, zero resolves) keeps the single-value path
byte-identical. Wired into both live-evidenced paths: PlayArtistSongs (after
its JF-448 pin, sharing the view) and PlaySong (restricted to the
generic-music-word shape so a REAL song title keeps today's scoped title
search). Review-round fixes that rode along: the confirm leg's artist query now
orders by popularity (YesIntentHandler.PlayArtist), and the four plain ask
builders now supersede stale cross-media decline keys (a same-session leak
where "no" answered an old not-found). NO locale strings added (all 17 locales
already carry DisambiguateMultipleArtists/DisambiguateReprompt); no model, no
NLU fixtures, no E2E changes (the behavior is handler-side; the JF-688 device
round can add the shared-word probe). The remaining 5 musician-slot call sites
are JF-702. Gates: Skill simplify (4 agents, applied/skipped above) + Skill
code-review high (9 findings: 7 applied, 1 filed as JF-702, 1 skipped with the
pool-cache reason). Suites 4921/4921 BOTH TFMs on the final state (baseline
4895 + 26). DoD 4-5 N/A (no session DTOs added beyond the existing MatchInfo
shape, no HttpClient changes); 6-7 N/A (no model change; handler pins are the
unit-level evidence, E2E covered by the existing artist matrix); 8 N/A (no
strings); 9-10 done (gates above). No deploy; do not push.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 N/A (the ask reuses the existing MatchInfo DTO shape; no new session attributes)
- [x] #5 N/A (no HttpClient changes)
- [x] #6 N/A (no interaction model change)
- [x] #7 N/A (no new intent; handler-level pins are the JF-690 evidence; the e2e artist matrix is unchanged)
- [x] #8 N/A (no new strings: the prompt reuses DisambiguateMultipleArtists, present in all 17 locales)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
