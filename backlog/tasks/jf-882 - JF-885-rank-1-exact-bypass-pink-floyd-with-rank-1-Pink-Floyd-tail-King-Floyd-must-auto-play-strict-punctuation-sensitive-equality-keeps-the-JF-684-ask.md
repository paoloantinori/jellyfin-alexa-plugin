---
id: JF-882
title: >-
  JF-885: rank-1-exact bypass - "pink floyd" with rank-1 Pink Floyd + tail King
  Floyd must auto-play (strict punctuation-sensitive equality keeps the JF-684
  ask)
status: Done
assignee: []
created_date: '2026-10-13 19:43'
updated_date: '2026-10-13 21:28'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to JF-884 (found by the 2026-10-13 evening e2e retry, still red after the JF-884 deploy). Live ER evidence for "metti una canzone dei pink floyd" (it-IT, profile-nlu raw dump): the musician slot's ER values are [Pink Floyd (id 7d23a278), King Floyd (id 53daafaa)] - Amazon matches King Floyd too on the shared "floyd" token. TWO distinct library artists, so the deployed JF-884 gate asks (correct per the two-distinct rule), but the ask is noise: rank-1 "Pink Floyd" is the EXACT match of the spoken query. The e2e fixtures (it-IT canzone dei pink floyd; es-ES/MX oye los; fr-FR lis les) legitimately expect the auto-play.

FIX: extend the MultiValueErDisambiguation survivor logic with a RANK-1-EXACT bypass: when Amazon's rank-1 canonical strictly equals the normalized spoken query, auto-play that artist and skip the ask (the tail values are coincidental token overlaps). STRICTNESS RULE (the JF-684 non-regression): the equality is case-insensitive and whitespace-normalized but PUNCTUATION-SENSITIVE, so "pink" vs "P!nk" must NOT bypass (different punctuation -> the JF-684 two-artist prompt stays), while "pink floyd" vs "Pink Floyd" (case only) DOES bypass. Source of truth for the spoken query: the raw slot value (careful: the raw slot value here is the VARIANT string "P!nk floyd" per the JF-884 finding - compare BOTH the raw slot value AND, if they differ, treat carefully: the right comparison target is what the user SPOKE; with the raw slot carrying a variant, use the strict-normalized compare against BOTH rank-1 canonical and the raw value, and bypass only if rank-1 matches the raw value strictly OR the raw is itself a punctuation-variant that fuzzy-resolves ONLY to rank-1's artist... simplest sound rule: bypass when rank-1 canonical strictly equals the raw slot value after case/whitespace normalization, since Amazon DERIVES the slot value from the same recognition; document the reasoning and pin both shapes).

GUARDS: (a) THE case: rank-1 Pink Floyd + tail King Floyd + query pink floyd auto-plays Pink Floyd; (b) JF-684 stays: "pink" with values [P!nk, Pink Floyd] still asks (punctuation blocks); (c) JF-884 stays: variant-only collapse auto-plays; (d) Pearl Jam collaboration family still asks; (e) no-canonical free-text path unchanged. Update the e2e fixtures only if the live behavior after the fix matches their expectation (they expect auto-play: after the fix they should pass as-is; if a fixture still disagrees, re-examine before touching it).
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
DESIGN CORRECTION (orchestrator, before dispatch): the 'rank-1 == raw slot value' strict rule is UNSOUND for the live case - Amazon sets slot.value to the matched SYNONYM rendition ('P!nk floyd'), which never string-equals the rank-1 canonical ('Pink Floyd'), so that rule would not fix the failing e2e. The principled rule is the JF-420 MARGIN at the ER layer: score every ER canonical (its resolved artist name) against the slot rendition via FuzzyMatcher; if rank-1's score minus the best other value's score > the JF-420 auto-select margin (20), auto-play rank-1 (dominant match; the tail is coincidental token overlap). Checks: pink floyd -> Pink Floyd ~high vs King Floyd ~weak: margin > 20, plays. JF-684 pink -> [P!nk, Pink Floyd] both score comparably vs 'pink': margin small, asks. The punctuation-sensitivity concern is subsumed: 'P!nk' vs 'pink' fuzzy-scores below exact, so dominance only arises when rank-1 is genuinely the user's ask. Keep the strict-equality bypass ONLY as a fast path when rank-1 canonical exactly equals the rendition.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf885 (82405f61), merged 67d7e89a, review round 2 (0ad3c779: kana romanization in the dominance leg, bounded token-sharing guard scan, co-calibration note, test-mock dedupe; the unresolved-tie dispositioned as load-bearing for the live case), pushed, DEPLOYED to minix. Rank-1 dominance bypass: TryResolveDominantRank1Artist scores resolved artists against the rendition (kana romanized per JF-755); rank-1 wins by the JF-420 margin (>= at the bar, probe-measured: the live shape sits exactly at 20 under both renditions) with the stale-catalog guard (token-bounded pool scan); auto-plays Pink Floyd over the King Floyd tail; JF-684 'pink' still asks (margin -15); Pearl Jam family still asks (margin 10). ORCHESTRATOR DECISION: rank-1 exactness stays NON-bypassing (the exact fast path flips the pinned Pearl Jam ask; margin-only is the maximal guard-keeping subset). Gates: worker /simplify (3+2) + worker code-review high (6 findings dispositioned); orchestrator /simplify four-angle pass clean + code-review high round 2 (3 applied, 2 dispositioned). Tests 5942/5942 BOTH TFMs; Release 0 warnings. E2E fixtures untouched (they expect the auto-play); their live green awaits the simulate outage clearing.
<!-- SECTION:FINAL_SUMMARY:END -->
