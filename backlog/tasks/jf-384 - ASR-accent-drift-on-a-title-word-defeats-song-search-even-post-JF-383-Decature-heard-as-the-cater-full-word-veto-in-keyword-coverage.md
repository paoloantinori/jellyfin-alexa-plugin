---
id: JF-384
title: >-
  ASR accent-drift on a title word defeats song search even post-JF-383
  ("Decature" heard as "the cater", full-word veto in keyword coverage)
status: To Do
assignee: []
created_date: '2026-08-20 09:30'
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
