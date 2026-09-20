---
id: JF-598
title: >-
  Movie fuzzy ranks substring 'Cicada' over prefix-token 'Ada' for the bare
  query 'ada' (JF-381 band shape, video path)
status: Done
assignee: []
created_date: '2026-09-20 10:43'
updated_date: '2026-09-20 15:57'
labels:
  - bug
  - search
  - fuzzy-matching
milestone: Polish
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found during the 2026-09-20 E2E run + fixture repins: the library contains 'Ada: My Mother the Architect' (2025), and a bare 'ada' query in PlayVideoIntent answers the did-you-mean flow suggesting 'Cicada' ('Non ho trovato ada. Intendevi Cicada?'). The video-title fuzzy evidently ranks the substring-containing 'Cicada' over the prefix-token 'Ada: ...'. This is the same shape as the JF-381 artist containment band (a coincidental substring match must not beat a prefix match), on the movie path. The E2E fixtures now honestly pin the current did-you-mean behavior; this task is the ranking fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Reproduce via simulator: 'metti il film ada' (it-IT) answers the did-you-mean flow suggesting 'Cicada' although the library contains 'Ada: My Mother the Architect' (a prefix-token match on 'ada')
- [x] #2 Ranking fix follows the JF-381 containment-band principle: a candidate whose title TOKEN starts with the query ('Ada: ...' token 'Ada' vs query 'ada') outranks a candidate that merely CONTAINS the query as a substring ('Cicada')
- [x] #3 E2E fixtures for the ada movie rows updated back to a play expectation once the ranking lands (currently pinned to 'Intendevi' honestly)
- [x] #4 Suite green both TFMs
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped in 390d1842, deployed and live-verified. Root cause sharper than the task description: EVERY containment-class candidate scores exactly ContainmentScore (90) and FindBestMatchWithScore early-returns at the band, so the winner among 'Cicada' (interior) and 'Ada: My Mother the Architect' (token prefix) was simply whoever the DB listed first. Fix: at the early return, PreferTokenPrefixContainment re-scans the candidate source for a query-at-token-boundary containment rival and swaps it over an interior incumbent - pure ordering among equal scores (nothing rejected, no score changed, all decision-point gates unchanged), which the /simplify review confirmed sits inside the matcher's recall contract (the JF-408 reverts demoted candidates; KeywordMatcher.PositionalBonus is the in-matcher shape-precedent). CRITICAL design point, now documented in the helper's remarks: the rival scan deliberately SKIPS the maxLenDiff band - for 'ada' the band is 15, excluding the 28-char Ada title, so a band-respecting scan would never find the rival this exists for; containment guarantees the claimed score so the swap is floor-correct. 5 tests incl. source-order independence and the reverse-containment (U2) guard. LIVE: deployed, simulator 'ada' now answers the screen-required tell (the Ada movie matched; no more 'Intendevi Cicada'); the 4 e2e ada fixtures repinned back to the screen-gate expectation and passing live (3 passed/1 skip). /simplify findings 1-3 applied (band-bypass doc, non-nullable signature, comment marker); finding 4 noted: the phonetic overload keeps first-wins among 90-scorers (its artist-path callers carry the JF-377/408 gates; revisit that early-return if the shape ever reproduces there). Suite 4158/4158 both TFMs; Release 0 warnings.
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
