---
id: JF-384
title: >-
  ASR accent-drift on a title word defeats song search even post-JF-383
  ("Decature" heard as "the cater", full-word veto in keyword coverage)
status: Done
assignee: []
created_date: '2026-08-20 09:30'
updated_date: '2026-08-20 20:03'
labels:
  - bug
  - search
  - song-search
  - asr
  - accent-drift
  - phonetic
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE REPRO 2026-08-20 (same session as JF-383, user on-device it-IT):

User said "Decature Street" (correct English pronunciation of the intended title "Decatur St."); it-IT ASR transcribed the slot as "the cater street" (PlaySong corr=658e6bc9 and corr=bdad82fe, logs 09:35:45). The abbreviation fix (JF-383, commits f52b990 + 7aa891a) does NOT cover this case: it is ACCENT DRIFT on the first word, a different failure class.

Token analysis (post stop-word + canonicalization):
- query "the cater street" -> [cater, street]
- title "Decatur St." -> [decatur, street]

Path behavior:
- PlaySong artist-songs fallback + FindSong artist-scoped (KeywordMatcher.Score): require 100% keyword coverage -> "cater" not in title tokens -> MISS. Correct-by-design for precision, but defeats the whole match on one drifted word.
- Exact Levenshtein: cater/c decatur ~71% on that word, irrelevant under 100% coverage gate.
- Phonetic: DM("cater")=KTR vs DM("decatur")=TKTR -> NO collision (unlike Koop/cup which both code KP). So the Double Metaphone layer cannot bridge this either: the drift added a leading consonant, it did not substitute an equivalent sound.
- FindSong global n-gram path stage 2 (SearchPhonetic) uses 50% keyword coverage: "street" matches post-canonicalization (1/2 = 50%, at the threshold) -> the GLOBAL path may already partially resolve this. The ARTIST-SCOPED path has no phonetic/partial-coverage stage. Verify which paths already handle it before designing.

RELATED, DISTINCT WORK:
- JF-383 (shipped): orthographic abbreviation (st<->street). Same repro utterance, different token.
- JF-381 (shipped, artists): phonetic floor for code collisions. Does not apply: no DM collision here.
- JF-379 (designed, catalog layer): phonetic synonyms for slot filling. Complementary; would fix it at the NLU layer for catalog artists/albums but song titles are free-text.

LIKELY DIRECTION (to validate): relax the artist-scoped keyword path to a partial-coverage score (mirror SearchPhonetic's >=50% keyword coverage with a score penalty) so one drifted word out of >=2 does not veto the match, OR per-word phonetic+fuzzy fallback for the non-matching keywords, gated to keep the precision discipline (multi-word queries only, like CrossMediaArtistMaxWords; single drifted word in a 1-word query must still miss).

VERIFICATION CASE (real library): PlaySong slot "the cater street" + "twilight singers" -> plays "Decatur St."; garbage control (e.g. "xyzzyfoo street") still not-found.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Reproduce the full chain at unit level: query tokens [cater, street] (post stop-word + canonicalization) vs title 'Decatur St.' tokens [decatur, street]: confirm KeywordMatcher.Score (100% coverage) misses and quantify SearchPhonetic's 50%-coverage behavior on the same input
- [ ] #2 #2 Decide the fix altitude (see analysis: per-word partial-coverage scoring in the artist-scoped path mirroring SearchPhonetic's 50% gate, vs relying on the phonetic stage) with the JF-337/JF-377 false-positive discipline (a wrong-song substitution is worse than a clean not-found)
- [ ] #3 #3 Implement + TDD: unit test locks 'the cater street' + 'twilight singers' resolving to 'Decatur St.' AND a guard that pure-garbage queries still miss (no Soul-Train-style false positive)
- [ ] #4 #4 /simplify + /code-review high before commit
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED 2026-08-20 (commit 5a1c916, deployed to minix, live-verified with the exact failed ASR string).

TWO root causes found (the second only via live testing):

1. FULL-WORD VETO (the task's original analysis): the exact keyword matcher requires 100% coverage, so one accent-drifted word ('cater' from 'Decature') killed the whole match; DM cannot bridge (KTR vs TKTR, no collision - unlike Koop/cup). FIX: phonetic second stage (KeywordMatcher.ScorePhonetic, >=50% coverage + 0.75 penalty, behind PhoneticSongSearchEnabled) in the two artist-scoped paths (FindSong artist search, PlaySong with-musician title fallback) - the same stage-2 semantics the global n-gram path always had. The un-drifted word ('street') carries the match; single garbage keyword (0%) still misses both matchers.

2. CROSS-LOCALE ENGLISH STOP WORDS (found during live verification): 'the cater street' under it-IT kept 'the' (not an Italian stop word) -> tokens [the, cater, street] -> phonetic coverage 1/3 = 33% < 50%, sinking even the phonetic stage (unit tests passed only because they used en-US). Also a PRE-EXISTING asymmetry: the n-gram index is built with en-US (strips 'the' from titles) while queries used the user locale. FIX: Tokenize always strips the English stop-word set in addition to the locale set. Deliberate contract change (empty-locale test updated with documentation); ja-JP particle guard unaffected.

AC #1 DONE: diagnostics tests lock the behavior (Score misses, ScorePhonetic finds at 50%, single garbage misses both).
AC #2 DONE: altitude = reuse existing ScorePhonetic on the same bounded set; no new scoring policy, no Score relaxation. FP discipline verified by review: multiple candidates disambiguate (not silent auto-play), single candidate auto-plays ANNOUNCED within the correct artist scope, flag-off-able, mirrors shipped global semantics.
AC #3 DONE: TDD, 8 new tests; 'the cater street' + 'twilight singers' -> 'Decatur St.' locked at unit level in both handlers; garbage control locked.
AC #4 DONE: review scans clean (phonetic stage + cross-locale blast radius: all empty-token paths guarded, symmetric, invariant on canonical outputs holds). Known accepted edges: single-English-stopword titles ('On'/'It') were already unreachable via the en-US-built index; residual pre-existing locale-stopword asymmetry noted in review, not introduced here.

VERIFICATION: 2651 tests green, Release 0 warnings. LIVE on minix with the EXACT failed device string: PlaySong 'the cater street' + 'twilight singers' (it-IT) plays 'Decatur St.' with the honest closest-match announcement ('Riproduco Decatur St., risultato piu' simile a...').
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
