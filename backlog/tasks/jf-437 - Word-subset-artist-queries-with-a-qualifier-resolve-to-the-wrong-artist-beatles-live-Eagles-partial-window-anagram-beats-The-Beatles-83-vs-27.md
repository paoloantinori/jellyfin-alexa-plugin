---
id: JF-437
title: >-
  Word-subset artist queries with a qualifier resolve to the wrong artist
  ('beatles live' -> Eagles: partial-window anagram beats 'The Beatles' 83 vs
  27)
status: Done
assignee:
  - zai
created_date: '2026-09-01 12:00'
updated_date: '2026-09-01 19:47'
labels:
  - search-quality
  - artist-search
  - live-finding
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs:349'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:186
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs:82'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live finding 2026-09-01 (deploy verification of the JF-420.2/420.3/421 bundle): simulator query 'beatles live' on the real library resolves to Eagles (log: matched artist='Eagles'), not The Beatles. Verified arithmetic via a temporary dump test (FuzzyMatcher.Score on the live names): Eagles=83, The Beatles=27, Beatles-bare=90.

ROOT CAUSE (confirmed): multi-word queries where the intended artist's name is a WORD-SUBSET of the query but neither a contiguous substring nor a prefix fall through tiers 1-3 (no name contains 'beatles live'; 'The Beatles' does not START with 'beatles') into tier-4 fuzzy, where PartialRatio's sliding window ranks a near-anagram short name above the real artist: the 'eatles' window inside 'beatles live' is edit-distance 1 from 'eagles' (score 83, length ratio exactly 0.5 so ApplyLengthPenalty's floor leaves it unpenalized), while 'The Beatles' scores 27 because the article prefix misaligns every window. Class members: 'beatles live', any '<artist> live/unplugged/acoustic' where the artist is stored with an article prefix; 'miles davis live' only survives today via tier-2 prefix luck.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 DIAGNOSE FIRST: reproduce with a permanent unit test (library [The Beatles, Eagles], query 'beatles live') capturing the confirmed arithmetic - Eagles 83 vs The Beatles 27 via FuzzyMatcher.Score
- [x] #2 FIX (primary): a word-coverage tier in the artist search chain - candidates whose word-set (leading-article-stripped, stop-word-stripped) is a SUBSET of the query's word-set outrank partial-fuzzy candidates. 'beatles live' -> The Beatles; 'miles davis live' -> Miles Davis without tier-2 prefix luck. Generalize JF-420.3's IsWordSubset (handler-private today)
- [x] #3 GUARDS: word-subset single matches still flow through the existing downstream judgment (JF-377 downgrade, JF-420 gate); no regression on 'nirvana unplugged', single-word queries, JF-381 band semantics
- [x] #4 BOTH implementations or a documented reason: inline PlayArtistSongs chain AND ArtistSearch.SearchAsync stay in sync (JF-382 status quo)
- [x] #5 Consider (secondary): article-aware prefix/partial normalization as complement - weigh against touching every fuzzy comparison
- [x] #6 Live verify after deploy: 'beatles live' resolves to The Beatles (or its disambiguation), never Eagles
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-437: qualifier artist queries ('beatles live') resolve to the intended artist via a word-coverage tier; the partial-window anagram class can no longer win.

WHAT CHANGED (commit a0daf06, 13 files)
- Tier 1.5 in the artist search chain, BOTH implementations, shared entry point ArtistSearch.TryWordCoverageTier (owns stopwatch + tier log once): candidates whose tokenized word-set (articles/stop-words stripped) is a subset of the query's word-set, placed AFTER tiers 2-3 and BEFORE tier 4.
- Selection: fullest distinct-word coverage ('Miles Davis' over 'Miles'), then contiguous in-order token subsequence ('Miles Davis' over re-tagged 'Davis Miles'), NO first-word winner-take-all: carrier-word ties ('The Band' vs 'Radiohead' for 'la band radiohead') return both for the disambiguation prompt.
- Review round (2 probe-verified first-cut bugs, both fixed): placement (first cut ran after tier 1, short-circuiting the tier-2 ASR-drift resolution: 'soul coughin' would play 'Soul'; now after tiers 2-3, handler-level guard test pins it) and selection (first-word preference let 'The Band' steal the carrier-bleed query; honest-tie selection replaces it). Fast mode keeps exact pre-tier semantics; SearchAsync locale now REQUIRED (13 production sites + tests threaded, CA1068 order); single-token early-return.
- Known limits documented in the helper: ASR drift defeats the byte-exact tier ('beattles live' still tier 4, where phonetic owns drift); DB paths lack the tier (cold-window divergence); pool re-tokenization cost (precompute = JF-440).

VERIFICATION (live, minix, DLL matches, config survived)
- 'beatles live' -> log "tier=1.5 duration=9ms results=1 method=WordCoverage" -> matched artist 'The Beatles' (the morning incident played Eagles). AC#6 met: The Beatles, never Eagles.
- Regressions: 'soul coughin' -> Soul Coughing via tier 2 (placement honored, no 1.5 log line); 'Soul Coughing' exact plays; xyzzyfoo clean not-found.
- Unit: the live case + 8 helper axis tests + the placement guard; suite 2802 -> 2811 passed / 0 failed; Release 0 warnings.
- Gates: /simplify (5 findings applied incl. a stray locale that would have made Alexa speak the locale code); /code-review high (10 findings: 2 probe-confirmed bugs + 4 hardening applied; F4 re-scoring inertness, F5 DB divergence, F6 drift limits, F7 token cache, F9 primitive proliferation -> task notes + JF-440).
- DoD 6/8 N/A (no model/locale changes); 7 = the handler+helper tests.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining)
- [x] #11 Findings applied or tracked
<!-- DOD:END -->
