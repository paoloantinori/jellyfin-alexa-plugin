---
id: JF-743
title: >-
  JF-743 - the caller-side Take(3) legs: PlayAlbum's alphabetically ordered
  direct search hits can drop the EXACT album name past rank 3, and PlayPodcast
  passes the full search-hit list into the helper's cap
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - ux
  - disambiguation
dependencies:
  - JF-735
references:
  - 'backlog/tasks/jf-735 - JF-735-AskFirstMatch-truncates-its-STATE-list-at-Take3-ranks-past-3-unaskable-and-unannounced.md'
  - 'backlog/tasks/jf-729 - JF-729-the-capped-multi-artist-ask-presents-its-3-spoken-names-as-an-exhaustive-found-set-no-and-others-hint-in-the-17-locale-strings.md'
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
the dropped rank-4+ tail is user-relevant rather than sub-40 noise (the other
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
name-relevant candidates even though they are sub-40 by the fuzzy judgment
that gated the ask. Whether cycling past 3 through such a tail is worth the
extra no-presses (versus the JF-735 noise-tail verdict) is this leg's
decision to make; if widened, the JF-729 hint applies and the helper cap
would need to stop being the binding truncation for this caller (pass the
full list into a widened state, or Take(N) with N decided here).

BAND CORNER (JF-735 code-review finding, filed here because it widens both
legs' user-relevance case without changing the helper verdict): the
NotFound-gating premise has a narrow exception. FuzzyMatcher's length band
skips candidates whose name differs from the query by more than
max(2 x query length, 15) BEFORE scoring (the cut is on the ABSOLUTE length
difference), and HandleFuzzyMiss treats the resulting null as NotFound, so
an UNSCORED containment-class candidate can sit in a NotFound-gated
AskFirstMatch list in EITHER length direction: a very short spoken query
inside much longer names (e.g. "u2" against "U2 Live from Red Rocks
Arena"), and symmetrically a much shorter NAME inside a long spoken query
(e.g. spoken "u2 live from red rocks arena" against the item "U2"). Among
SCORED candidates the sub-threshold-noise invariant holds at the default
threshold (an exact name scores 100 and either-string-contains-the-other
scores 90, both diverting to auto-play or the Confirm ask); the one config
exception is a user-set FuzzySuggestionThreshold above 90, which can admit a
scored 90-class candidate into a NotFound list. The JF-735 verdict kept the cap
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
- [ ] #1 Leg 1 decided and shipped (exact pick-off / rank-to-front / widened state with hint) or consciously declined with the reason recorded here
- [ ] #2 Leg 2 decided and shipped or consciously declined with the reason recorded here
- [ ] #3 dotnet build passes with 0 errors, no new warnings
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
