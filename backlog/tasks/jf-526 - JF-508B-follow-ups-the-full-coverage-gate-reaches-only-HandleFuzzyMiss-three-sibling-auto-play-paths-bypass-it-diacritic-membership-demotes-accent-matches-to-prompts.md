---
id: JF-526
title: >-
  JF-508B follow-ups: the full-coverage gate reaches only HandleFuzzyMiss -
  three sibling auto-play paths bypass it; diacritic membership demotes accent
  matches to prompts
status: Done
assignee: []
created_date: '2026-09-09 05:17'
updated_date: '2026-09-09 15:48'
labels:
  - fuzzy-matching
  - routing
  - follow-up
dependencies: []
references:
  - JF-508
  - JF-408
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-508 part B code review (2026-09-09, SAFE TO MERGE with same-turn tracking conditions) and simplify pass:

(1) SIBLING BYPASS (review Important finding 2, CONFIRMED): three auto-play paths never reach the new gate - SearchMediaIntentHandler ~:211 (site-level FuzzyMatch pre-check auto-plays and returns before HandleFuzzyMiss), BaseHandler.PlayPlaylist ~:4832 (same pre-check shape), BaseHandler.SearchItemsFuzzyAsync ~:2474 (the zero-result fuzzy fallback used by PlayBook/PlayPodcast/PlayVideo/PlayPlaylist). The corr=269e622d misfire ('soul coffee', score 72) would still auto-play ungated if it arrived via any of these. The gate is dead code at those sites; extract the predicate and apply it at all four decision points.

(2) DIACRITIC DEMOTION (review finding 1, CONFIRMED mechanism): the gate's exact byte-membership demotes accent-variant matches from silent play to prompt, including above-ContainmentScore cases ('besame mucho' vs 'Bésame Mucho' = 91, previously played with no qualifier at all; 'cafe del mar' under it-IT = 2 tokens post-stop-word). Tradeoff not a bug - one extra 'yes' turn - but the codebase's own ScorePhonetic precedent treats phonetic equality as coverage. Decide and implement the membership fallback.

(3) Riders from the simplify pass: R1 coverage-sharing (KeywordMatcher owns the definition), S3 repro-test helper reuse.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The gate predicate extracted so all four auto-play decision points share one definition: HandleFuzzyMiss (gated), SearchMediaIntentHandler ~:211 site-level pre-check, BaseHandler.PlayPlaylist ~:4832 pre-check, BaseHandler.SearchItemsFuzzyAsync ~:2474 zero-result fallback (the three currently BYPASS the gate - the JF-508 misfire shape survives there ungated; 'soul coffee' still auto-plays via SearchMedia)
- [ ] #2 Diacritic/phonetic membership decided: exact byte-membership demotes above-ContainmentScore accent matches ('besame mucho' vs 'Bésame Mucho' = 91, previously silent play) to a prompt; either fold diacritics (Normalize FormKD + strip marks) or accept Double-Metaphone equality as coverage (the ScorePhonetic precedent), with tests for 'besame'/'cafe del mar'/'coop' shapes
- [ ] #3 Coverage-sharing consolidation (R1): one full-keyword-coverage definition in KeywordMatcher instead of the gate's HashSet re-implementation (Score's admission check delegates to it)
- [ ] #4 Test helper reuse (S3): the JF-508 repro test re-inlined the harness wiring RunFuzzyMiss/AssertScoreInRange centralize
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped, merged (6396c671 + merge), deployed (md5 54373dd4, clean boot on the 12.0 box, 24 queues, zero FTL, play smoke green after settle). The short-query full-coverage gate now lives in ONE definition, KeywordMatcher.HasFullKeywordCoverage, applied at ALL FOUR auto-play decision points: HandleFuzzyMiss's score-bar disjunct (delegates), SearchMedia's site-level pre-check and PlayPlaylist's pre-check (both fall into HandleFuzzyMiss's yes/no prompt on a gated miss - verified: single prompt, no double-prompt, nothing dropped), and SearchItemsFuzzyAsync's zero-result fallback (feeds 10 handler families: gated miss logs a withhold LogDebug and returns null = the below-threshold outcome, indistinguishable no more). Membership is diacritic-folded (FormD + strip NonSpacingMark, coverage-only, documented why NOT Jellyfin.Extensions' table-based RemoveDiacritics): 'besame mucho' vs 'Bésame Mucho' (91) silently plays again, 'cafe del mar' it-IT passes, 'coop' vs 'Koop' still fails (phonetic-equality is not membership). AutoPlay users are exempt at all four sites (review F1: the code-review caught the policy contradiction at the fourth site - fixed with the guard + regression test; parity with HandleFuzzyMiss's disjunct and PlayAlbum's guard). 15 tests (14 worker + 1 F1; the three sibling tests adversarially verified to bite). Gates: /simplify (A1 withhold-log + R1 fold-doc applied; S2 rejected with the CA1068/CS1737 mechanism - the required-locale reorder is analyzer-incompatible with the method's existing token-mid-list shape, documented), code-review high FIX FIRST (single finding F1, applied). Tests 3545/3545 on BOTH TFMs. DoD 4-8 N/A.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
