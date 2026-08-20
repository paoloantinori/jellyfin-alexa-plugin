---
id: JF-383
title: >-
  Abbreviation mismatch in song title search: spoken "Street"/"Road"/"Avenue"
  never matches tagged "St."/"Rd."/"Ave."
status: Done
assignee: []
created_date: '2026-08-20 06:40'
updated_date: '2026-08-20 07:35'
labels:
  - bug
  - search
  - song-search
  - asr
  - tokenizer
  - i18n
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/19'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE REPRO 2026-08-20 (user report + log analysis):

User asked Alexa (it-IT) for "Decature Street" by The Twilight Singers. Three failures:
1. PlaySong corr=658e6bc9: ASR transcribed "Decature Street" as "the cater street". Exact Jellyfin SearchTerm query -> 0 songs.
2. FindSongByArtist corr=a4b0938c: artist resolved correctly to The Twilight Singers (id 062bd039, 33 songs).
3. FindSong corr=afe3b57e with keyword="street": 0 matching songs. ROOT CAUSE: the library track is titled "Decatur St." (abbreviated). KeywordMatcher tokenizes on punctuation -> title tokens [decatur, st]; the keyword token "street" matches neither exactly nor phonetically (DM: street->STRT vs st->ST).

ROOT CAUSE CLASS: orthographic abbreviation mismatch (St.<->Street, Rd.<->Road, Ave.<->Avenue, No.<->Number, Pt.<->Part), NOT phonetic drift. Distinct from the Koop/cup problem: Double Metaphone cannot bridge it (same word, different orthographic realizations). The catalog synonym layer (JF-379) does not help either: this is query/index-side title matching.

Music taggers commonly abbreviate street addresses and track parts in titles, so this recurs for every abbreviated title.

LIKELY FIX (per session analysis 2026-08-20): abbreviation expansion at tokenization - a small canonical map applied in KeywordMatcher.Tokenize (or as synonyms in SongNgramIndexService's index) so a spoken "street" matches a tagged "st." token (and vice versa). Keep it bidirectional and bounded to the common music-title abbreviations. Files: Alexa/Util/KeywordMatcher.cs, Alexa/SongNgramIndexService.cs, handlers that tokenize titles/keywords (FindSongIntentHandler 3-stage search).

VERIFICATION CASE: library track "Decatur St." (The Twilight Singers) must be found by (a) FindSong keywords="street" with artist filter; (b) ideally also the full "decatur street" phrase. Simulator-testable locally (no hardware).

Also noted during repro (secondary, not this task): the catalog contains folder-derived artist entries ("The Twilight Singers - She Loves You" with 0 own songs) that pollute entity resolution. Separate cleanup if it recurs.

Upstream GitHub issue: see the corresponding issue in paoloantinori/jellyfin-alexa-plugin (linked in comments once created).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Abbreviation map (street/st, road/rd, avenue/ave, number/no, part/pt at minimum) applied in title/keyword tokenization so a spoken full word matches an abbreviated tagged token (and the reverse)
- [ ] #2 #2 Unit tests: KeywordMatcher/n-gram search finds title 'Decatur St.' with keyword 'street' (and the reverse direction: keyword 'st' matching 'Street')
- [ ] #3 #3 Simulator live-verify on minix: FindSongByArtist 'twilight singers' + keyword 'street' resolves and plays 'Decatur St.'
- [ ] #4 #4 No regression: existing keyword-search tests unchanged; token count/purity for non-abbreviated titles unaffected
- [ ] #5 #5 /simplify + /code-review high pass before commit
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED 2026-08-20 (commit f52b990, deployed to minix, on-device confirmation pending).

Fix: abbreviation equivalence map applied inside KeywordMatcher.Tokenize, post stop-word filter, so title index and spoken keywords canonicalize identically (bidirectional match): st/saint->street, rd->road, ave->avenue, pt->part, vol->volume.

AC #1 DONE. Map as specified PLUS saint added to the st class (/code-review finding: tagged "St." is ambiguous between Street and Saint, "St. Louis Blues" vs "Decatur St."; the canonical token is a class representative, extra cross-matching acceptable in coverage-oriented search). number/no EXCLUDED with documented reasoning: "no" is a real en/it word, a Japanese grammatical particle, and a Portuguese stop word; canonicalizing it would corrupt token streams (hard guard test: "watashi no uta" untouched).

AC #2 DONE. 6 new TDD tests (written first, verified failing): the exact Score integration (title "Decatur St." found by keyword "street"), full-map bidirectionality, the saint path, ja-JP particle guard, similar-token guard (stone/roadster/storage untouched).

AC #3 PARTIAL (tooling limitation, honestly stated): the intent simulator cannot carry session attributes across turns, so the 2-turn FindSong flow (artist, then keywords) cannot be driven end-to-end with existing tooling; the E2E harness is single-turn too. Verified instead: (a) unit integration test at the exact changed layer, (b) deployed active DLL contains the fix (identifier check), (c) n-gram index rebuilt at restart with the new tokenizer (12766 songs logged). ON-DEVICE confirmation pending: user retry "Decature Street by the twilight singers" on the Echo. If a follow-up is wanted, a small task could add session-attribute injection to the simulator (would enable multi-turn testing generally).

AC #4 DONE. 2642 tests green (0 regressions), Release 0 warnings. All 6 stop-word locale sets verified literally: neither map inputs nor outputs are stop words anywhere (documented as a LOAD-BEARING INVARIANT).

AC #5 DONE. /simplify (3 agents: consolidated to single-point post-filter loop, added invariant note), /code-review high (bug scan found the Saint ambiguity -> addressed; in-file comments agent found a wording imprecision -> fixed), /review-local gate (5 agents): NO findings >= 80.

GH issue #19 open for the external report; comment with fix + release note pending next release.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
