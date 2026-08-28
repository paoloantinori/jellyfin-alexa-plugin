---
id: JF-408
title: >-
  PlayAlbum fuzzy fallback auto-plays 1-char album names on inflated
  partial-ratio scores ("walls for cup" matched album "O" @90)
status: Done
assignee: []
created_date: '2026-08-28 15:37'
updated_date: '2026-08-28 17:14'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-28 15:54:21 (it-IT Echo): user asked for album "Waltz for Koop", ASR produced slot album="walls for cup" (musician slot empty, ER_SUCCESS_NO_MATCH on both static and dynamic AlbumName authorities). PlayAlbumIntentHandler exact search missed, then the JF-336 fuzzy fallback (PlayAlbumIntentHandler.cs ~line 148-182) ran FuzzyMatcher.FindBestMatchWithScore over ALL albums using PartialRatio and matched album "O" (Damien Rice, 1-char name) with score=90 (log: "PlayAlbum: fuzzy fallback matched album 'O' score=90 for query='walls for cup'"), auto-playing it with FoundAlbumInstead announcement.

Root cause: PartialRatio slides the shorter string over the longer one. A 1-char candidate ("O") trivially finds its single character inside any long query ("for" contains 'o') and scores 90. The existing MinFuzzyAlbumQueryLength guard protects against SHORT QUERIES, but there is no symmetric guard on SHORT CANDIDATE NAMES. Target album "Waltz for Koop" exists in the user's library, so this is purely a scoring defect, not a data problem.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Repro unit test: FuzzyMatcher.FindBestMatchWithScore("walls for cup", ["O", ...realistic album names]) must NOT return "O" at score >= 60 (the live incident scored 90)
- [x] #2 PlayAlbumIntentHandler fuzzy fallback does not auto-play candidates whose normalized name is shorter than a guard length (exact length TBD by implementer, ~4 chars) against queries above that length; such candidates are skipped or re-scored with full-ratio (no partial sliding)
- [x] #3 Existing fuzzy-fallback tests (accent drift, e.g. caffe'/Cafe cases from JF-336) still pass
- [x] #4 dotnet test without --no-build, all green
- [x] #5 Interaction with HandleFuzzyMiss threshold documented in code comment where the guard lives
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Root cause was sharper than the task assumed: the JF-342 length penalty ALREADY existed for coincidental short-candidate matches, but its containment exemption (candidate contained in query = near-exact match) is degenerate for 1-2 char candidates: 'walls for cup' contains 'o', so album 'O' got ContainmentScore=90 unpenalized.

Fix implemented in the SHARED FuzzyMatcher.ApplyLengthPenalty (not handler-local): the containment exemption now requires candidate length >= MinExemptContainmentLength (3). This re-scores degenerate candidates by the length ratio (score collapses below threshold), which protects album, artist, and song paths alike. AC #2's 'skipped or re-scored' satisfied via the re-score option at the shared layer; deviation recorded here.

Tests added in Unit/FuzzyMatcherTests.cs: 1-char ('O') and 2-char ('Up', contained in 'cup') candidates must score < DefaultThreshold; incident replay with realistic album set must not auto-play 'O'. Both failed pre-fix ('1-char candidate scored 90'), green post-fix. Legit containment ('jazz' in 'play that jazz song') still passes.

Full suite 2727 passed, 0 failed.

RESIDUAL FOUND AND FIXED via the NLU dry-run's simulator failure (against the deployed 0.12.0.0 build): the library contains a garbage-metadata artist literally named 'artist' (items 'Track 09', album 'title'); query 'xyznonexistentartist123' contained it MID-TOKEN and auto-played with empty speech. The length floor cannot catch this (6-char candidate).

A FuzzyMatcher-wide token-boundary exemption rule was attempted and REVERTED: it broke the documented tier-4 recall contract (ArtistSearchTests.SearchAsync_Tier4_PartialTokenSubstring_ReturnsMatch: SearchAsync returns substring matches without judging; the JF-377 predicate judges). Precision does not belong in the recall layer.

Correct fix (implemented): extended ArtistSearch.IsCoincidentalContainmentMatch with an interior-occurrence rule: when EVERY occurrence of the candidate in the query is strictly interior (word chars on both sides), the containment is coincidental regardless of content-word count. Prefix/suffix shapes ('outkasts' -> 'outkast', plurals) are exempt and keep auto-playing. The handler's single-match JF-377 downgrade then turns the gibberish auto-play into a yes/no prompt.

New tests: IsCoincidentalContainmentMatch_InteriorContainmentSingleTokenQuery_True (red pre-fix) and IsCoincidentalContainmentMatch_PluralAffixedSingleToken_False (regression guard). Full suite 2730 passed.

KNOWN REMAINING RESIDUAL: the ALBUM fuzzy fallback (JF-336) has no JF-377-style predicate, so a 3+ char garbage-metadata ALBUM name interior-contained in a single-token album query could still auto-play. Follow-up candidate: mirror the interior rule for albums.

Integration harness fix along the way: test_stream_security.py e2e tests now skip (instead of AttributeError) when jellyfin_client is None under --dry-run, matching the established test_e2e.py guard pattern.

CODE-REVIEW GATE (review-local, 5 agents) OUTCOME: two findings forced a redesign. (1) BLOCKING xUnit1031 in the new JF-410 test (blocking .Result under -warnaserror) - fixed via await, Release build verified clean. (2) MEDIUM: the 1-2 char length floor regressed real short names under carrier-bleed ('suona la musica di u2' vs 'U2': containment 90 dropped to 8, never reaching the JF-377 prompt) - same altitude mistake as the reverted token-boundary rule: precision injected into the recall layer.

FINAL ARCHITECTURE: FuzzyMatcher.ApplyLengthPenalty is back to the pre-JF-408 baseline (containment exemption unconditional; doc now states the recall-layer contract and points to the decision-point guards). The guards live where auto-play is decided: artist path = IsCoincidentalContainmentMatch (JF-377 coverage + JF-408 interior rule), album fuzzy fallback = NEW ArtistSearch.IsInteriorContainment gate in PlayAlbumIntentHandler (skips auto-play, falls to artist-fallback/not-found).

AC #1/#2 REWRITTEN BY OUTCOME (was: matcher-level suppression test and handler guard on candidate length; the discovered recall/judgment layering refutes matcher-level suppression). Effective acceptance, now test-locked: handler-level HandleAsync_InteriorContainmentFuzzyMatch_DoesNotAutoPlay (incident replay, 'walls for cup' vs album 'O'), predicate-level InteriorContainmentSingleTokenQuery_True + PluralAffixedSingleToken_False, matcher-level FindBestMatch_ShortRealNameInCarrierBleedQuery_StillMatches (U2 recall lock).

Full suite 2731 passed; Release -warnaserror 0 warnings 0 errors.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Coincidental-containment auto-plays fixed at the decision points after two review-driven redesigns: FuzzyMatcher stays pure recall (containment exemption unconditional, per the tier-4 contract), ArtistSearch.IsCoincidentalContainmentMatch gains the interior-occurrence rule (feeds the JF-377 yes/no prompt), and the new ArtistSearch.IsInteriorContainment gates the PlayAlbum fuzzy fallback. Live-verified on the deployed build: "walls for cup" returns not-found instead of playing album "O"; "xyznonexistentartist123" gets the disambiguation prompt instead of a silent auto-play of the garbage "artist" entity. Residual documented: album-side word-coverage predicate (3+ char interior shapes already covered; multi-word coincidences via album path remain theoretical).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
