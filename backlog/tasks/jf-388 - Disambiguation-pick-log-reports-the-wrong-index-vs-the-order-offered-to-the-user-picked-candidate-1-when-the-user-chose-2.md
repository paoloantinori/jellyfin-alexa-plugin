---
id: JF-388
title: >-
  Disambiguation pick log reports the wrong index vs the order offered to the
  user ('picked candidate #1' when the user chose #2)
status: To Do
assignee: []
created_date: '2026-08-21 09:32'
labels:
  - bug
  - logging
  - disambiguation
  - triage
  - low-priority
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live evidence 2026-08-21 11:28 (corr 3f897a9a -> 265a7e83):

FindSong disambiguation offered 'Ho trovato 2 canzoni. 1. St. Gregory, 2. Decatur St.. Quale?' The user answered '2' and the skill correctly played Decatur St. (candidate #2 in the offered list). BUT the debug log printed 'FindSong: disambiguation picked candidate #1: Decatur St.' - the '#1' label does not match the position offered to the user (#2).

Root cause (probable, to verify): the log counts from the internal candidate collection index or a re-scored order, not from the presented order. The SELECTION is correct (the right item played); only the label is misleading. This wastes triage time: a future session reading 'picked #1' after the user said '2' would suspect an off-by-one selection bug that does not exist.

Also noted from the same exchange (observation only, no action): 'St. Gregory' surfaced as a candidate for 'the cater street' via the JF-384 phonetic 50%-coverage stage ('street' matches the 'St.' token post-canonicalization). Technically correct per the coverage-oriented design; listed here as context for why multi-candidate disambiguation fires more often since JF-384.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Correct the disambiguation log line to report the 1-based index in the SAME order presented to the user (the Candidates list order), or clearly distinguish internal ordering from presented ordering
- [ ] #2 #2 Verify the ordinal selection path (user says '2' -> picks the 2nd item in the offered list) is consistent with the corrected log; add/update unit test
- [ ] #3 #3 No behavior change to the actual selection logic (it is correct); log-only or ordering-label fix
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
