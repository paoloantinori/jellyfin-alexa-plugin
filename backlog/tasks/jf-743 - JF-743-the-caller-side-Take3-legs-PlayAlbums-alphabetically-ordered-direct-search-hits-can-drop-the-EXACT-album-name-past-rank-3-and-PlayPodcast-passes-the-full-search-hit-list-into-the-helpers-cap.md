---
id: JF-743
title: >-
  JF-743 - the caller-side Take(3) legs: PlayAlbum's alphabetically ordered
  direct search hits can drop the EXACT album name past rank 3, and PlayPodcast
  passes the full search-hit list into the helper's cap
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-04 12:50'
labels:
  - ux
  - disambiguation
dependencies:
  - JF-735
references:
  - >-
    backlog/tasks/jf-735 -
    JF-735-AskFirstMatch-truncates-its-STATE-list-at-Take3-ranks-past-3-unaskable-and-unannounced.md
  - >-
    backlog/tasks/jf-729 -
    JF-729-the-capped-multi-artist-ask-presents-its-3-spoken-names-as-an-exhaustive-found-set-no-and-others-hint-in-the-17-locale-strings.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn from the JF-735 weighing evidence (the
review-recommendation rule: the findings are real, outside the helper-level
decision JF-735 declined, and land in the tracker the moment they are
reported). JF-735 declined to widen AskFirstMatch's state because a
helper-side change cannot reach the truncations that actually bite: nine of
the ten multi-candidate AskFirstMatch callers pre-truncate with their own
Take(3) at the call site. This task covers the two of those ten legs where
the dropped rank-4+ tail is user-relevant rather than sub-threshold noise (the other
eight are NotFound-gated fuzzy tails, where the defense proven in JF-735
holds and no change is wanted).

LEG 1 (the strong one, exact match reachable and silently unaskable):
PlayAlbumIntentHandler's direct-search multi-match leg (~line 683) does
`albums.OrderBy(Name).Take(3)` into AskFirstMatch. Unlike every other
multi-candidate caller this leg is NOT gated on HandleFuzzyMiss weakness: it
fires for ANY multi-album SearchTerm result with distinct names (Confirm-mode
users), and the JF-427 divergence deliberately keeps the alphabetical order
as the pick policy. Jellyfin's SearchTerm token-matching plus alphabetical
ordering means the EXACT album the user named can sit past rank 3 (concrete
shape: query "rush" over a library holding "Gold Rush", "Love Rush",
"Midnight Rush", and "Rush"; the exact name sorts 4th). There is no
exact-name pick-off before the ask (PlayPodcast has one, the JF-640 exact
pass; this leg does not). The user hears three partial matches, can cycle to
exhaustion, gets NoMoreMatches, and never learns the album they named was in
the list: the JF-729 exhaustive-found-set defect in its strictest form, at
the caller rather than the helper. Candidate fix shapes to weigh: an exact
(EqualsIgnoreCase) pick-off or rank-to-front before the cap, and/or widening
this caller's Take to the AskMultipleArtists full-state-plus-hint shape
(the DisambiguateMultipleArtistsMore key already exists in all 17 locales).

LEG 2 (the weaker one, relevant-but-fuzzy tail silently dropped):
PlayPodcastIntentHandler's multi-candidate leg (~line 235) is the ONLY
AskFirstMatch caller that passes an UNTRUNCATED list; the helper's internal
Take(3) is what caps it. The list is SearchTerm hits for the podcast name
across both storage shapes (MusicAlbum + Series), so rank 4+ entries are
name-relevant candidates even though they are sub-threshold by the fuzzy judgment
that gated the ask. Whether cycling past 3 through such a tail is worth the
extra no-presses (versus the JF-735 noise-tail verdict) is this leg's
decision to make; if widened, the JF-729 hint applies and the helper cap
would need to stop being the binding truncation for this caller (pass the
full list into a widened state, or Take(N) with N decided here).

BAND CORNER (JF-735 code-review finding, filed here because it widens both
legs' user-relevance case without changing the helper verdict): the
NotFound-gating premise has a narrow exception. FuzzyMatcher's length band
skips candidates whose name differs from the query by more than
max(2 x query length, 15) BEFORE scoring, and HandleFuzzyMiss treats the
resulting null as NotFound, so an UNSCORED containment-class candidate, a
very short spoken query inside much longer names (e.g. "u2" against "U2
Live from Red Rocks Arena"), can sit in a NotFound-gated AskFirstMatch
list. The band is ONE-DIRECTIONAL by arithmetic: it scales on the QUERY
length (maxLenDiff is always >= the query length), so only candidates
LONGER than the query can be skipped; a shorter candidate is never
band-excluded, and any either-direction containment hit it would score
lands at ContainmentScore (90) and diverts, so the reverse-direction shape
(spoken "u2 live from red rocks arena" against the item "U2") is SCORED at
90 and cannot sit unscored in a NotFound list. Among SCORED candidates the
sub-threshold-noise invariant holds at the default threshold; the one
config exception is a user-set FuzzySuggestionThreshold above 90, which can
admit a scored 90-class candidate into a NotFound list. The JF-735 verdict kept the cap
despite the corner (pre-existing band behavior shared by every acceptance
surface; the binding truncation at nine of ten sites is the caller-side
Take(3) anyway), but a caller-side fix here is the natural place to absorb
it.

PIN SHAPE for whoever picks this up: leg 1, a PlayAlbumIntentHandler test
seeding 4+ distinct-name albums whose alphabetical order buries the exact
name at rank 4 (assert today's red behavior first: the exact name is absent
from the stored state), then the fix's shape (exact reachable first-turn or
cyclable). Leg 2, a PlayPodcast test seeding 5 SearchTerm hits asserting the
stored state's count per the decision. GUARDRAIL (scope stated exactly): a
NEW or REMOVED AskFirstMatch call site trips AskFirstMatchCallerCensusTests
(the JF-735 IL census); changing the helper's cap trips the
FirstMatchStateCap state pins; a caller-side LIST-SHAPING change (dropping a
Take(3), or Take(N) with N > 3) trips NEITHER guard, so re-weigh the census
on DisambiguationHelper.FirstMatchStateCap's doc by hand in the same sitting
before shipping one.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Leg 1 SHIPPED (rank-to-front pick-off, the "plays or asks FIRST" shape): PlayAlbumIntentHandler's direct-search multi-match leg now orders `albums.OrderByDescending(IsExactNameMatch(effectiveAlbumTerm, a.Name)).ThenBy(Name, OrdinalIgnoreCase)` before its unchanged Take(3). The exact name is the Confirm ask's FIRST spoken/cycled candidate and the AutoPlay pick (albums[0]); the non-exact remainder keeps the JF-427 alphabetical order byte-identically (no exact present: single ordering key + stable sort reproduces today's order). The comparison is the ONE canonical JF-420.1 predicate ArtistSearch.IsExactNameMatch (case-insensitive, end-trimmed; /simplify reuse round), keyed on effectiveAlbumTerm because the JF-469 calling-word-stripped retry (the one producer that keeps `album` raw for not-found speech fidelity) queries the stripped term (code-review altitude round; red-proven below). The widened-state-with-hint option was NOT taken: the filing's pin language (stored-state first entry) maps to rank-to-front, the JF-341 Confirm contract (distinct-name multi-match asks) is preserved, and no new locale key is needed. RED PROOFS, all live on both TFMs: the Confirm pin failed pre-fix at state[0] (Expected "Rush", got "Gold Rush"; the exact name absent from the stored state entirely), the AutoPlay pin failed pre-fix at the played-token assert (Gold Rush's track played for query "rush"), and the JF-469-feed pin failed with the effectiveAlbumTerm assignment removed (state[0] "Gold Rush" again), restored, green. No AskFirstMatch call site added or removed, Take(3) kept: census and state pins untouched (guardrail re-weigh recorded on FirstMatchStateCap's doc in the same sitting)
- [x] #2 Leg 2 CONSCIOUSLY DECLINED, evidence below and locked by a pin: PlayPodcast's untruncated tail keeps the helper's FirstMatchStateCap as its binding truncation. The lock: HandleAsync_FiveSubThresholdSearchHits_NotFoundAskKeepsThree (five SearchTerm hits, none exact, all sub-suggestion-threshold; stored state count 3, first-three names in the list's arbitrary albums-then-series order). No JF-729 hint treatment, no 17-locale string
- [x] #3 dotnet build Release --no-restore -warnaserror on the final state: Build succeeded, 0 Warning(s), 0 Error(s)
- [x] #4 dotnet test on the final state: 5120/5120 net9.0 AND net10.0 (baseline 5116 + 4 new pins: the two burial pins, the JF-469-feed pin, the decline lock). Runs executed: full suite once mid-flight (5119/5119 both TFMs, before the review rounds) and once on the final state; the final-state net10.0 leg 5120/5120, the net9.0 leg of that same invocation had ONE transient failure (name not captured by the summary-only grep; NOT in any touched suite; the identical net9.0 suite re-ran 5120/5120 clean, and every touched suite passed in isolation on both TFMs after every round, 155/155). No repro across two subsequent full runs; reported here per the fix-every-failure rule rather than dismissed
- [x] #5 /simplify + /code-review high PASSED (literal Skill calls in the worker transcript). /simplify 4 agents: efficiency CLEAN (verified LINQ keys evaluate once per element, N+1 predicate calls is the floor); reuse 2 APPLIED (the canonical IsExactNameMatch predicate replacing both inline string.Equals sites; SetupAlbumsAndTracks replacing pin 2's hand-rolled GetItemList wiring) + 1 optional note SKIPPED with reason (widening GetPlayedTrackTokenAsync with a user parameter: drive-by refactor of a shared helper, the file idiom inlines this block for custom-user pins); simplification 2 APPLIED (banner collapsed to one line; FirstMatchStateCap doc session-history/tracker-status clauses cut, durable outcomes kept) + 1 RESOLVED BY THE REUSE FIX (the gated LogDebug stays: the exact-only signal is the debug policy's branch marker, and the lockstep hazard dissolved once both sites call the one named predicate); altitude 1 APPLIED (the JF-469 producer gap: effectiveAlbumTerm shadow + new pin, red-proven by sabotage). /code-review high: 0 correctness bugs, 4 low-severity findings ALL APPLIED (the false "same-name duplicates all equal the query" comment premise corrected to the real stable-sort rationale; the LogDebug gained the matched item id per the CLAUDE.md debug policy; the JF-469 pin now asserts the raw-then-stripped SearchTerm sequence, closing the raw-first contract blind spot; OrderByDescending on the positive predicate + the hoisted exactRankedFirst bool). No real-but-out-of-scope finding survived, so the reserved JF-749 number stays unused
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
LEG 1 (shipped, rank-to-front): the exact pick-off on PlayAlbum's direct-search
multi-match leg is a single ordering change,
`OrderByDescending(IsExactNameMatch(effectiveAlbumTerm, name)).ThenBy(name,
OrdinalIgnoreCase)`, placed before the unchanged Take(3) so it feeds BOTH
consumers: the Confirm ask (the exact name is candidate 0, first spoken, first
cycled; a "no" walks the alphabetical remainder) and the AutoPlay pick
(albums[0] is the album the user named, no longer the alphabetically-first
partial match). The predicate is the ONE canonical JF-420.1
ArtistSearch.IsExactNameMatch (case-insensitive, end-trimmed; the /simplify
reuse round caught the first cut hand-rolling string.Equals beside the
predicate its own comment cited). The keying term is effectiveAlbumTerm, not
`album`: the /code-review altitude round proved the JF-469 calling-word
stripped retry (the one producer that deliberately keeps `album` raw for
not-found speech) queries the STRIPPED term, so a raw-keyed pick-off went
inert exactly there and the burial defect survived one retry later
("chiamato rush" -> query "rush" -> Gold/Love/Midnight Rush + Rush, exact at
rank 4 again); the shadow assignment in the retry branch closes it, and the
JF-489/JF-492 producers needed nothing (they already reassign `album` to the
queried term; JF-411 and the fuzzy fallback produce single-item lists that
bypass the block). THREE red proofs, all live on both TFMs: the Confirm pin
failed pre-fix at state[0] (Expected "Rush", got "Gold Rush"; the exact name
absent from the stored state), the AutoPlay pin failed pre-fix at the played
token (Gold Rush's track), and the JF-469-feed pin failed with the shadow
assignment removed (sabotage), green on restore. Byte-identity for no-exact
lists holds by construction (single ordering key + LINQ stable sort), and the
same-name-duplicate path is untouched (full-key ties keep insertion order; the
review round corrected the first cut's false comment premise about WHY).

LEG 2 (declined with evidence): PlayPodcast's tail keeps the helper cap as its
binding truncation. The weighing: (1) the exact-name class, the only strongly
user-relevant rank-4+ shape, never reaches this ask at all (the JF-640 exact
pass filters the union to exactMatches BEFORE the multi-candidate leg; one
exact plays directly, multiple exacts re-enter where the exact scores 100 and
HandleFuzzyMiss diverts, so the NotFound ask's list is exact-free by
construction); (2) the list that does reach the ask passed HandleFuzzyMiss's
NotFound gate, so every SCORED candidate is below the suggestion threshold
(default 40), the same sub-threshold-noise invariant the JF-735 decline rests
on, with the identical one-directional length-band corner already documented
on FirstMatchStateCap (an UNSCORED containment candidate; FuzzyMatcher
untouched per the filing: context, not a work item); (3) the list order is
arbitrary albums-then-series database concatenation, so rank 4+ is unranked
tail, not deeper relevance, and a widened window cannot promise the wanted
candidate lands inside it; (4) Jellyfin SearchTerm token matching pulls in
article/short-token noise (a query sharing "the"/"il" matches every show
carrying it), reinforcing the noise classification; (5) the cost side is
structural: the binding truncation for this caller IS the helper's cap (the
JF-735 decline, locked by two state pins and the enforced census), so widening
means new widened-state helper surface plus a census roster edit with a
by-hand re-weigh plus a NEW 17-locale hint key (DisambiguateMultipleArtistsMore
is AskMultipleArtists-only and artist-worded), for a leg that fires only when
4+ SearchTerm hits are ALL sub-threshold with no exact name. The decline is
LOCKED by HandleAsync_FiveSubThresholdSearchHits_NotFoundAskKeepsThree (five
seeded sub-threshold hits; state count 3; the first-three names pin the
arbitrary-order fact the decision rests on).

GUARDRAIL honored: no AskFirstMatch call site added or removed (census
unchanged at PlayAlbum=2, PlayPodcast=2), Take(3) kept, helper cap untouched
(the JF-735 state pins stay green); the by-hand census re-weigh is recorded on
FirstMatchStateCap's doc in the same sitting, updated from "filed as JF-743"
to the closure state (leg 1 fixed caller-side, leg 2 declined with the lock
pin, band corner stays as documented).

GATES: /simplify 4 agents (efficiency CLEAN; reuse 2 applied + 1 skipped with
reason; simplification 2 applied + 1 resolved by the reuse fix; altitude 1
applied, the JF-469 producer gap with its own red proof). /code-review high:
0 correctness bugs, 4 low-severity findings all applied (comment-truth, the
debug-policy item id, the raw-first sequence assert, the
OrderByDescending/hoisted-bool shape). No out-of-scope finding survived, so
the reserved JF-749 number stays free. Suites: 5120/5120 net9.0 and net10.0
on the final state (baseline 5116 + 4 new pins); Release -warnaserror 0
warnings 0 errors. One transient net9.0 failure appeared in the final-state
invocation's first leg (name not captured by the summary grep, not in any
touched suite, not reproducible across the two subsequent full runs; every
touched suite 155/155 in isolation on both TFMs after every round). No
locale or interaction-model change (leg 2 declined needs no string), so the
validators are untouched. No deploy: the change is one handler + tests + doc;
the orchestrator's batched deploy owns the DLL.

CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit 447c87eb + orchestrator tail 8fa3735e, --no-ff; the gate-marker's one finding - the vacuous 'Rush' speech assert - applied by the orchestrator per the doc-only-tail rule), combined-tree suite 5126/5126 both TFMs, deployed in the batched post-closure deploy. Leg 2's decline is evidence-backed and pin-locked; JF-749 unused, no out-of-scope finding.
<!-- SECTION:FINAL_SUMMARY:END -->
