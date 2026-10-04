---
id: JF-742
title: >-
  JF-742 - fold the arbitration-plus-pool-threaded-search sequence (a sixth
  hand-synced site would be the extraction trigger)
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-04 08:17'
labels:
  - dedup
dependencies:
  - JF-734
references:
  - >-
    backlog/tasks/jf-734 -
    PlayArtistSongs-pool-threading-completion-and-the-deeper-view-memo-for-the-JF-715-preloadedPool-axis.md
  - >-
    backlog/tasks/jf-715 -
    JF-715-extract-the-multi-value-ER-gate-consume-composite-one-shared-shape-for-the-three-songmusician-handlers-and-thread-the-arbitration-pool-through-SearchAsyncs-fall-through-legs.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn by the JF-734 worker (reserved number), satisfying
the review-recommendation discipline: the JF-734 code-review high round flagged
a real structural finding whose fix spans handlers outside JF-734's enumerated
surface (PlayArtistSongsIntentHandler, ArtistSearch/SearchService, and their
tests), so it is skipped with a reason and filed here instead.

FINDING (code-review high, ranked 3 of 3): the "pin view -> TryArbitrate ->
ask-return -> survivor-adopt -> else SearchAsync(preloadedPool: pool)"
sequence is now hand-synced at FIVE pool-threading sites: the JF-715 composite
(MultiValueErDisambiguation.TryArbitrateOrSearchAsync, serving PlaySong,
AddToQueue, PlayNext), PlayArtistSongsIntentHandler (~235-276, threaded by
JF-734), QueryArtistLibraryIntentHandler (~138-159, JF-715), and
FindSongIntentHandler's two musician-supplied legs (~250-290, ~356-384,
JF-715). PlayAlbumIntentHandler is a sixth POOL CONSUMER in a deliberately
divergent shape (the arbitration is conditional on the empty-album branch and
feeds local state; the search sits in a separate block with its own JF-492
fallback), counted as a consumer, not a copy. The cost of the status quo: the
next SearchAsync axis or arbitration-contract change must be hand-applied to
five sites, and a missed one compiles clean and diverges silently, the exact
class JF-715 itself was filed to fix (its trigger was the same sequence at
three sites).

COUNTERWEIGHTS a fix must weigh (recorded by the JF-734 rounds, both reviews):
the existing composite structurally cannot host the artist-only twins without
growing four axes (it pins internally and returns no view, while
PlayArtistSongs needs the pinned view after the search for the JF-420 gate,
the Fast-mode fuzzy pick, and the JF-652 pool; it hardcodes default policy
axes while PlayArtistSongs passes mode/asrCompoundWordFixEnabled/
parallelDbTiers; it collapses results to List<Guid> plus one name while the
artist-only handlers consume IReadOnlyList<BaseItem> through judgment gates;
it hardcodes the NotFoundSongByArtist terminal while each twin owns a
different tail). A fold is therefore an ARTIST-ONLY composite (a sibling of
TryArbitrateOrSearchAsync, parameterized on the policy axes and the retry
label, returning the pinned view plus the artists list, tail left at the
handler), not an extension of the existing one. The JF-734 altitude review
argued even that sibling risks a parameter-swamped shape; the JF-734
code-review argued the five hand-synced copies are worse. This task exists to
settle that with the sites' actual variance on the table.

ACCEPTANCE SHAPE: either the artist-only composite lands (with the counting
pin family extended: each folded site's GetArtistsCalls==1 pin moves to drive
the composite), or the task closes with a documented decline naming the axis
variance that makes the sibling a net loss. Do NOT fold PlayAlbum (its
conditional-arbitration shape is the documented divergence, not an omission).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Decision recorded: artist-only composite extracted, or declined with the named axis-variance reason
  (EXTRACTED: the fold pays. The concrete design against the counterweights: the
  composite `MultiValueErDisambiguation.TryArbitrateOrSearchArtistsAsync` takes
  12 parameters, of which 3 are the policy axes WITH DEFAULTS (only
  PlayArtistSongs passes any), 1 is the REQUIRED `arbitrate` gate-condition axis
  (fail-open removed per the code-review F4 finding), and the rest are the
  standard context tuple the song composite already carries (its 11 params
  include 2 axes; the artist sibling actually needs FEWER axes than feared
  because the dbQuery channel is NOT a parameter at all: all four folded sites
  shared the byte-identical `RetryAsync(..., "GetArtists", ct)` channel, and
  BaseHandler.RetryAsync is a thin delegation to
  RetryHelper.ExecuteWithRequestBudgetAsync, so the composite builds it once
  with no label axis). No continuation parameter was needed: the outcome enum
  shape (the caller switches on Ask/Artists/empty) leaves every tail at the
  handler, verified against the four genuinely different tails. The verdict
  turned on the failure class being real and already bitten once: JF-734's
  Finding 1 was exactly a missed fifth site, and the JF-715 audit addendum
  itself missed two adopters.)
- [x] #2 If extracted: all four direct sites folded (PlayArtistSongs, QueryArtistLibrary, FindSong x2), pins green, no behavioral delta; if declined: this file carries the reason
  (all four folded; the ~30-line sequences, the per-site pins, and FindSong's
  ArbitrateMusicianMultiValue wrapper are gone. Pins: the JF-734 threading pin
  and the four folded-site GetArtistsCalls==1 counting pins (FindSong x2,
  QueryArtistLibrary, PlaySong/twins for the sibling composite) stay green
  UNCHANGED and now drive the composite, proven by a live red proof: nulled the
  artist composite's `preloadedPool` ONLY (the song composite untouched) ->
  exactly the four artist-site pins failed on BOTH TFMs (1 vs 2) while the song
  trio's pins stayed green, reverted, re-run green. The warming-choke pin
  (FindSong_MusicianLeg_ArtistIndexWarming_ThrowsAtTheChokePoint) green: the
  composite's UNGUARDED pin preserves the JF-419.2 choke. Same-view contract
  holds by construction (the composite owns the single pin; idempotent on the
  one pre-pinned caller). ONE behavioral delta was caught by the code-review
  high round (F1) and FIXED by keeping PlayArtistSongs on the caller-pins-first
  GUARDED shape: on a DISABLED index its Fast-mode pick passes the view to
  FuzzyMatchPhonetic, whose null branch runs the plain matcher overload (first
  ContainmentScore hit wins) while a not-ready view runs the phonetic overload
  (full scan above the floor), so a uniform unguarded pin could pick a different
  artist among multi DB hits; the site hands its guarded view in (Pin idempotent)
  and full pre-fold equivalence is restored, with the composite doc recording
  which sites the unguarded equivalence DOES hold for. PlayAlbum left unfolded,
  documented in the composite doc as the deliberate non-fold.)
- [x] #3 dotnet test passes both TFMs
  (full suite on the final state: 5093/5093 net9.0 (1m56s) and 5093/5093
  net10.0 (1m48s), exit 0, baseline 5093 unchanged, no new tests (the existing
  pin family covers the composite, per the red proof). An earlier full run
  showed one transient net9.0 failure later identified by the independent
  code-review round as the KNOWN flaky VideoAudioControllerTests.Dispose
  temp-dir class (it hit 5092/5093 in that round too, then passed 266/266 in
  isolation; untouched by this diff); three of my own subsequent full runs were
  green on both TFMs. Release -warnaserror over the whole tree: 0 warnings 0
  errors. Filtered runs along the way: the six-class affected battery 116/116
  both TFMs after every edit round, 126/126 after the review fixes.)
- [x] #4 /simplify + /code-review high passed
  (/simplify 4-agent round: reuse CLEAN (the sibling split verified
  non-removable at source; the retry-channel duplication is 2 lines with
  legitimately different label policies); efficiency CLEAN (per-leg parity
  verified against the minus lines; two dismissed deltas: one SnapshotView
  alloc on the disabled path, one async state-machine box, both parity-class);
  altitude CLEAN on all four points (the `arbitrate` axis verified NOT
  expressible via rawConstraintSlot without loosening the JF-702 contract; the
  pin unification right depth; no remaining hand-synced site: the remaining
  SearchAsync callers run no arbitration; right class); simplification 1
  finding APPLIED (the disabled-index parenthetical deduplicated between the
  PlayArtistSongs site comment and the composite doc) + 1 nit skipped with
  reason (a private DbArtistChannel helper for the two channel lambdas would be
  new abstraction, not reuse; the song composite's retryLabel precedent already
  covers a future differing label). /code-review high: 5 findings, 4 APPLIED
  (F1 the guarded-pin restoration above, verified at source against
  FuzzyMatcher's two overload structures before applying; F3 the composite
  regained the per-leg observability the deleted FindSong wrapper owned: three
  debug lines mirroring the song sibling, the survivor line carrying the Id; F4
  `arbitrate` made REQUIRED with the two true-sites passing it explicitly,
  closing the fail-open; F5 the CLAUDE.md handlers-vs-legs wording), 1 SKIPPED
  with reason (F2 direct composite tests: the unpinned disabled leg it worried
  about no longer exists after F1, the leg matrix is covered end-to-end by the
  handler suites with the red proof demonstrating the pins drive the composite,
  and the choke shape is pinned by SkillWarmingUpTests). The reviewer
  independently ran the full suite mid-review and identified the transient
  failure class.)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the JF-742 worker on its worktree branch. VERDICT: the fold pays and
landed. The artist-only composite `MultiValueErDisambiguation
.TryArbitrateOrSearchArtistsAsync` (sibling of the song trio's
`TryArbitrateOrSearchAsync`, on the class that already owns the gate) now owns
the "pin, TryArbitrate, ask-return, survivor-adopt, else SearchAsync(pool)"
sequence at all four direct sites; the outcome `ArtistGateSearchResult` returns
Ask, the full IReadOnlyList<BaseItem> (survivor as a single-element list,
uniformly adopted the way every site did inline), the gate's Pool, and the
PinnedIndex view; every terminal tail stays at the handler. The parameter shape
beat the counterweights: the dbQuery channel is not an axis at all (all four
sites shared the byte-identical "GetArtists" RetryHelper channel, built once
inside), the 3 policy axes default (only PlayArtistSongs passes any), and the
one new axis `arbitrate` is REQUIRED (code-review F4), encoding the sites'
structural gate conditions FindSong's legs genuinely cannot express through the
generic-word probe (keywords-empty is stricter than IsGenericSongConstraint).
Per-site dispositions: PlayArtistSongs folded but KEEPS the caller-pins-first
guarded pin (code-review F1: its Fast-mode FuzzyMatchPhonetic pick is the one
consumer where null and a not-ready empty-snapshot view diverge, verified
against FuzzyMatcher's two overload structures); QueryArtistLibrary and both
FindSong legs pass the live index and ride the composite's unguarded pin (choke
preserved, pinned by the warming test); FindSong's ArbitrateMusicianMultiValue
wrapper deleted, its collapse-log duty moved to the composite's survivor debug
line (with the Id restored); PlayAlbumIntentHandler left hand-threaded, the
documented non-fold (interleaved JF-489/JF-492 shape), noted in the composite
doc. Pins: the whole counting family green unchanged and driving the composite,
live red proof (artist-composite-only sabotage -> exactly the four site pins
fail on both TFMs, song pins green). CLAUDE.md's preloadedPool paragraph updated
(the threading now lives in the composite at four handler LEGS at three
handlers; a raw grep counts four call sites). Suites: 5093/5093 both TFMs on
the final state (baseline unchanged), Release -warnaserror clean; one transient
net9.0 blip in an early run identified as the known flaky
VideoAudioControllerTests.Dispose class by the independent review round (passes
in isolation; untouched). Gates: simplify 4 agents (1 applied, 1 nit skipped
with reason, three angles clean), code-review high 5 findings (4 applied, 1
skipped with reason; none filed as JF-744, no out-of-scope finding survived).
Production surface changed (MultiValueErDisambiguation, three handlers); deploy
owned by the orchestrator's batched post-closure deploy.

ORCHESTRATOR CYCLE 2026-10-04: merged into main (worker commit c0042071 + gate-marker tail eba1fe7a, --no-ff), quad-merged tree suite verified (the pre-existing isolation-green flake noted on net9.0, full green net10.0), CI green, deployed in the batched post-closure deploy. The orchestrator gate-marker verified all six axes at source and re-ran the affected classes itself (180/180 both TFMs); its two findings applied in the tail (the closed-vs-skipped gate log word; the CLAUDE.md grep-exactness), and the tail's simplify gate closed clean (efficiency/altitude/reuse CLEAN with the ternary traced and the grep live-counted, one comment-trim nit applied).
<!-- SECTION:FINAL_SUMMARY:END -->
