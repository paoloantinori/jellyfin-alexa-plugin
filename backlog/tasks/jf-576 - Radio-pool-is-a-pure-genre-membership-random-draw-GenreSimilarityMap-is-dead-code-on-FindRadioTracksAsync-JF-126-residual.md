---
id: JF-576
title: >-
  Radio pool is a pure genre-membership random draw: GenreSimilarityMap is dead
  code on FindRadioTracksAsync (JF-126 residual)
status: Done
assignee: []
created_date: '2026-09-16 11:01'
updated_date: '2026-09-16 12:49'
labels:
  - search-quality
  - radio
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Secondary finding from the JF-574 investigation (2026-09-16): the AutoPlay/radio pool built by RadioTrackSource.FindRadioTracksAsync is a PURE genre-membership random draw (Genres = seed.Genres, Limit=50, OrderBy Random, ShuffleAndCap 15/20). GenreSimilarityMap (shipped in JF-126 for small result sets) is DEAD CODE on this path: nothing consults it when the seed genre yields few tracks. Consequence: on the Norah Jones incident shape the radio replacement drew 15 arbitrary Jazz tracks with no similarity ranking, compounding the false-exhaustion bug. Scope: (1) verify with a reading pass that GenreSimilarityMap truly has zero callers on the FindRadioTracksAsync path; (2) either wire similarity ranking into the pool build (seed-artist/genre adjacency ordering before ShuffleAndCap) or delete the dead map and close JF-126's residual claim honestly; (3) small-result-set behavior is the original JF-126 motivation, so preserve or supersede that contract explicitly. File under area B (search/catalog quality).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
<!-- SECTION:IMPLEMENTATION_NOTES:BEGIN -->
2026-09-16: WIRE verdict chosen; the wiring was small and behavior-scoped.

- Reading pass: GenreSimilarityMap.GetSimilarGenres/HasSimilarGenres had ZERO production callers (grep over the plugin project: only the map itself and its own test file). FindRadioTracksByGenreAsync confirmed as a single GetItemList (Genres filter, Limit 50, Random); callers confirmed at PlaybackNearlyFinishedEventHandler.cs:657 and :719 (item-seeded) and PlayRadioIntentHandler.cs:174 and :199 (genre-seeded and item-seeded).
- Design: expansion is CONDITIONAL on thin primary results, which preserves JF-126's small-result-set motivation as the trigger instead of discarding it. Primary seed-genre query runs first, unchanged; only when it returns fewer than GenreSimilarityMap.ExpansionThreshold (5) deduplicated items does exactly ONE extra GetItemList run with the similar genres appended after the seeds (seed genres ranked first, case-insensitive dedup, capped at MaxExpandedResults=50 genres). Rich libraries keep the exact single-genre pool and never pay the second query; the Alexa response-budget risk is bounded to at most two 6s-budgeted retry calls. Admission is still Jellyfin genre membership, so the FuzzyMatcher recall-vs-judgment rule does not apply (pool construction, not admission).
- TDD: 4 new tests written first and run RED (2 failed on the old single-query behavior), then GREEN after the RadioTrackSource change. Two pre-existing characterization tests (ByGenre_BuildsTheRadioGenreQueryShape, FindRadioTracks_SeedsFromTheItemsGenresAndExcludesTheItemItself) pinned the single-query shape and were updated to the new contract: the former now feeds a rich primary result so it still asserts exactly one query; the latter asserts queries[0].Genres and documents that the thin primary result legitimately fires the expansion query.
- Evidence: dotnet build (Debug, both TFMs) 0 errors; dotnet test full suite GREEN on BOTH TFMs: net9.0 3980/3980 passed, net10.0 3980/3980 passed; dotnet build -c Release 0 warnings.
- Files changed: Jellyfin.Plugin.AlexaSkill/Alexa/Util/RadioTrackSource.cs (QueryGenresAsync extraction + conditional expansion + ExpandGenres helper); Jellyfin.Plugin.AlexaSkill.Tests/Unit/RadioTrackSourceTests.cs (4 new tests, 2 characterization updates). Nothing committed; status left In Progress.
- Known limitation: the map covers 20 broad English genre keys; an unmapped seed genre (e.g. a subgenre string like "vocal jazz") gets no expansion (no second query, pool unchanged). Deliberate: expanding with no data would be a no-op query. Enriching the map is a data task, not a code task.
<!-- SECTION:IMPLEMENTATION_NOTES:END -->
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FIXED (commit 3b9d9ea6). Verdict: WIRE, not delete. The previously-dead GenreSimilarityMap (JF-126 residual) now drives thin-result expansion in the radio pool: the primary seed-genre GetItemList (Limit 50, Random) runs first unchanged; only when it returns fewer than ExpansionThreshold (5) deduplicated items does ONE extra GetItemList run with the expanded genre filter (seeds lead the expanded FILTER array; result order stays Jellyfin's Random sort). Rich libraries never pay the second query. JF-126's small-result-set motivation is preserved as the TRIGGER, per its original intent. Review-driven hardening: both queries share ONE Alexa budget (the expansion receives only the time the primary left unspent and never fires below RetryHelper.DefaultMinOperationMs; the code-review major found two full 6s retry budgets back-to-back could reach ~12s and blow the ~8s window). ExpandGenres moved onto GenreSimilarityMap beside its constants (MaxExpandedResults doc corrected to genre-name semantics, belt-and-braces cap noted). Tests: 4 new red-first (2 failed pre-change), 2 characterization tests updated, item-seeded expansion assertion strengthened to Assert.Equal(2, queries.Count) + expanded-filter equality (review minor: the old Assert.True(Count >= 1) let a dropped expansion pass). Gates: /simplify 4-angle pass applied (merge-loop AddAll dedup, comment trims, relocation, history-sentence removal); code-review high via feature-dev:code-reviewer applied both findings. Suite 3980/3980 both TFMs, Release 0 warnings. Known bound: the map covers broad English genre keys only; an unmapped seed genre (e.g. 'vocal jazz') gets no expansion and today's pool (enriching the map is a data task, not code).
<!-- SECTION:FINAL_SUMMARY:END -->
