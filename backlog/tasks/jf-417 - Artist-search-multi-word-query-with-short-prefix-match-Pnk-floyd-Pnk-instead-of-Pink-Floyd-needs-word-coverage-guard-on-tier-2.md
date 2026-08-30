---
id: JF-417
title: >-
  Artist search: multi-word query with short prefix match (P!nk floyd -> P!nk
  instead of Pink Floyd) needs word-coverage guard on tier-2
status: To Do
assignee: []
created_date: '2026-08-30 13:08'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live finding 2026-08-30 (on-device, user reports as unacceptable): Italian user says "pink floyd" on an it-IT Echo, ASR transcribes as "P!nk floyd" (the exclamation-mark artist name). The 4-tier artist search:
- Tier 1 (Contains): fails ("P!nk floyd" not in any artist name)
- Tier 2 (PrefixFirstWord "P!nk"): matches artist "P!nk" (4 chars, covers only 40% of the 10-char query) -> SHORT-CIRCUITS, never reaches tier 4
- Tier 4 (FuzzyAll) would have matched "Pink Floyd" at high score ("P!nk floyd" vs "Pink Floyd" is a near-exact fuzzy match)

Result: user asks for Pink Floyd, gets P!nk's "So What". This is documented in the E2E tests as expected behavior for the "P!nk floyd" ASR variant, but the user considers it unacceptable.

Root cause: the tier-2 prefix match accepts a candidate that covers only the first word of a multi-word query without checking if a better full-name match exists at a later tier. The JF-381 containment gate protects tier-1 from coincidental short-candidate matches, but tier-2 (prefix-shaped) has no equivalent coverage guard for multi-word queries where the candidate is much shorter than the full query.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When a multi-word query's tier-2 prefix match produces a candidate whose name covers only the FIRST word (candidate name length < 50% of full query length), do NOT short-circuit: continue to tier-3 (full-query prefix) and tier-4 (fuzzy-all) before accepting the match. If tier-4 produces a better-scoring full-name match (e.g. 'P!nk floyd' fuzzy-matches 'Pink Floyd' at ~85+ while 'P!nk' scores ~50 due to length penalty), the tier-4 result wins.
- [ ] #2 The word-coverage guard must NOT fire for single-word queries (the current ASR-truncation shape: 'crash' -> 'Crash Test Dummies' must keep working via tier-2).
- [ ] #3 The guard must NOT fire when the candidate name covers >= 50% of the query, even if multi-word (e.g. 'pink' -> 'Pink Floyd' should still match at tier-2 via prefix 'Pink').
- [ ] #4 Unit tests: (a) 'P!nk floyd' with artists [P!nk, Pink Floyd] in the library -> resolves to Pink Floyd (via tier-4), not P!nk; (b) 'crash' with artist [Crash Test Dummies] -> still resolves via tier-2 (single word, no guard); (c) 'pink' with artist [Pink Floyd] -> still resolves via tier-2 (coverage >= 50%: 4/10 chars).
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
