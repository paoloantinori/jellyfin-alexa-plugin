---
id: JF-977
title: >-
  Per-locale PlayLibrarySong twin fixture pins (15 locales uncovered): one
  carrier row + famous sentinel each, expectation live-verified before commit
status: To Do
assignee: []
created_date: '2026-11-27 05:57'
labels:
  - nlu
  - fixtures
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-940 closure round (2026-11-27): the twin-intent fold (PlaySongHandler claims PlaySongIntent + PlayLibrarySongIntent, the JF-874 shape) and the JF-946 catalog-backed SongTitle shipped in ALL 17 models and are live, but the PlayLibrarySong fixture pins exist only in the two archetype locales (de-DE 5 rows, it-IT 7 rows, both lab-verified 2026-11-22). The other 15 locales have no twin pin, so a locale-specific selection regression in the twin goes unseen until a live battery. DISCIPLINE (mandatory, the phantom-red rule): every new row's expectation MUST be verified against that locale's live model BEFORE committing the row (a bare recorded --live run per locale with the -k filter, or riding an existing planned battery); an unverified expectation is how phantom-reds happen. Row shape per locale: the locale's article/noun carrier for the song twin + a synthetic non-famous title (SongTitle static values are training guidelines, any title flows) expecting PlayLibrarySongIntent, plus the famous bare sentinel expecting PlaySongIntent. Suggested batching: ride the locale batteries that other work already schedules; do not launch 15 dedicated runs just for this.
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
