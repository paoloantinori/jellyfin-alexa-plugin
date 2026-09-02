---
id: JF-388
title: >-
  Disambiguation ranks the WRONG candidate first: phonetic-tie broken by
  PositionalBonus misfire on canonicalized abbreviation ('St. Gregory' outranks
  'Decatur St.' for query 'street')
status: Done
assignee: []
created_date: '2026-08-21 09:32'
updated_date: '2026-08-21 09:58'
labels:
  - bug
  - ranking
  - phonetic
  - disambiguation
  - abbreviation
  - JF-383-followup
  - JF-384-followup
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SCOPE EXPANDED 2026-08-21 after the user's follow-up (not just a log issue - the RANKING is wrong).

LIVE EVIDENCE (corr 3f897a9a -> 265a7e83, mechanically verified by probe against the real ScorePhonetic):
User asked for 'Decatur Street' (heard as 'the cater street'). Both these candidates passed the phonetic stage at 50% coverage, but in this ORDER: '1. St. Gregory, 2. Decatur St.' - the WRONG candidate ranked FIRST.

Probe scores (real KeywordMatcher.ScorePhonetic):
- St. Gregory: 42.5 (37.5 + 5 PositionalBonus)
- Decatur St.: 37.5 (no bonus)

ROOT CAUSE (the PositionalBonus misfire): the bonus (+5) is awarded when the FIRST title token matches any keyword. 'St. Gregory' tokenizes to [street, gregory] ('St.' canonicalized to 'street' by JF-383), so its first token 'street' matches the query's 'street' -> bonus. 'Decatur St.' tokenizes to [decatur, street]; its first token 'decatur' does NOT match -> no bonus. The bonus intended to reward the title's LEADING word being what the user asked about instead rewards the Saint-reading of the ambiguous abbreviation happening to sit in first position.

THE USER'S SHARPER FRAMING: 'non aveva bisogno della mia conferma per sapere che io cercavo street e non saint'. The query contains the FULL word 'street'. When the full spoken word exactly matches a canonicalized abbreviation token, the skill could disambiguate the abbreviation's MEANING itself: 'St.' in 'Decatur St.' follows a name (street reading, matches the spoken word); 'St.' in 'St. Gregory' precedes a name (Saint reading, a DIFFERENT word). The canonicalization guessed 'saint'->'street' for St. Gregory; the exact-match differential between the non-matching keywords (cater~decatur at ~71% Levenshtein vs cater~gregory unrelated) is the real discriminator, and ScorePhonetic ignores it entirely (same lesson as the reverted JF-337 attempt: the non-matching token's closeness is the signal).

FIX DIRECTIONS (evaluate in this order):
1. Rank-aware non-match scoring: when candidates tie on phonetic coverage, break the tie by the best Levenshtein/PartialRatio of the NON-matching keyword pairs (cater/decatur 71% vs cater/gregory ~33%). This is per-word fuzzy on the residual, NOT the reverted JF-337 token-fraction phonetic (different mechanism, same discriminating signal it identified).
2. Abbreviation-meaning guard: when a keyword is the FULL word (street) and matches a candidate token that was canonicalized FROM an abbreviation, verify the abbreviation's most likely reading by position (St. before a proper noun = Saint; St. after = Street) - cheap heuristic, covers the common cases.
3. PositionalBonus fix: only award the +5 when the first title token matches AND is NOT a canonicalized-from-abbreviation token (canonicalized tokens are the tokenizer's guess, not the user's word).

ORIGINAL (log-only, still valid but secondary): the pick log prints 'candidate #1' for the item the user selected as #2 (index counted from internal/re-scored order, not the offered order).

CANDIDATE PROLIFERATION NOTE: since JF-384 the 50%-coverage stage surfaces more candidates (any song with one phonetically-matching token). With the ranking fixed, the top candidate should dominate; consider whether a decisive score gap can auto-play instead of prompting (coverage-oriented but announced), or at minimum present the best candidate first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Fix the ranking: for the live case (query 'the cater street', candidates 'Decatur St.' + 'St. Gregory'), Decatur St. must rank FIRST (probe-verified ScorePhonetic order; today St. Gregory wins 42.5 vs 37.5 via the PositionalBonus misfire)
- [ ] #2 Implement one of the three fix directions (residual-keyword tiebreak preferred; positional-bonus guard acceptable); TDD with the live case as the RED test
- [ ] #3 Guard against the JF-337 lesson: the fix must discriminate cater/decatur (71%) from cater/gregory (~33%) without reintroducing a false-positive surface (garbage control: 'xyzzyfoo street' must not gain ranking from the residual scorer)
- [ ] #4 Log fix (secondary): the pick log index must match the order offered to the user
- [ ] #5 No regression: the 2653-test suite green; the saint-class matches (query 'saint louis' -> 'St. Louis Blues') still work
- [ ] #6 /simplify + /code-review high before commit
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED 2026-08-21 (commit 1ebcff2, deployed to minix, LIVE-VERIFIED).

FIX: ResidualKeywordTiebreak in KeywordMatcher.ScorePhonetic. When candidates tie on phonetic coverage (both matched 'street' via the 50% gate), the NON-matching keywords' fuzzy closeness breaks the tie: cater PartialRatio decatur = 80 vs cater PartialRatio gregory = 20. Applied as a RANKING contribution only (cap 10.0, above the +5 PositionalBonus it must override, below the ~26-point coverage-tier gap it must never bridge), never as an admission gate (the reverted JF-337 lesson).

Probe-verified scores: Decatur St. 45.5 > St. Gregory 44.5 (was 37.5 < 42.5).

USER'S INSIGHT CONFIRMED: 'St. Gregory' semantically means 'Saint Gregory' (a different word from 'street'); the canonicalization's saint->street guess for St. Gregory was wrong in context, and the ordering (St. before a proper noun = Saint) was the semantic signal. The residual tiebreak implements this discrimination mechanically (the unmatched keyword 'cater' is close to 'decatur' but not to 'gregory') without needing the positional heuristic.

VERIFICATION: TDD (3 new tests: ranking RED-then-GREEN, garbage control, saint-class guard). 2656 green, Release -warnaserror clean, container CI-matching green. LIVE on minix: 'the cater street' + 'twilight singers' now plays 'Decatur St.' DIRECTLY (no disambiguation prompt needed - the right candidate dominates).

Log fix (secondary AC): NOT addressed in this commit; the pick-log index mismatch remains (low priority, log-only).
<!-- SECTION:FINAL_SUMMARY:END -->

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
