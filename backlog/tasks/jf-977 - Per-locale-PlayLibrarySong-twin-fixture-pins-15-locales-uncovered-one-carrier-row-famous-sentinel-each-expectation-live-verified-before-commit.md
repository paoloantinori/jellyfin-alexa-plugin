---
id: JF-977
title: >-
  Per-locale PlayLibrarySong twin fixture pins (15 locales uncovered): one
  carrier row + famous sentinel each, expectation live-verified before commit
status: Done
assignee: []
created_date: '2026-11-27 05:57'
updated_date: '2026-11-27 06:24'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED not-applicable (2026-11-27, premise corrected against the generated models): PlayLibrarySongIntent + the SongTitle slot exist ONLY in de-DE and it-IT, BY DESIGN (commit 5abc965c: 'carrier-noun song slots for it-IT + de-DE, the JF-874 twin-intent shape, issue #46 phase 1'; templates carry it in exactly those two files). The 15 other locales never had the twin, so there is nothing to pin. THE CORRECT PER-LOCALE MECHANISM there is the stock free-text PlaySongIntent path, which is live-verified healthy: fr-FR (run 20261127-065633: every carrier incl. the colliding 'lis le titre' routes PlaySongIntent with the slot filled; the fr fixture pin landed yesterday in that shape) and en-US (the JF-964 battery: healthy free-text song slots, no conjunction disease). RECOMMENDATION RECORDED against rolling the twin out to the healthy 15: the twin is the CURE for the de-class MediaInfoType-collision disease; in healthy locales a catalog-backed SongTitle slot would INTRODUCE the JF-684 selection-gating risk (titles outside the user's catalog can stop selecting at all - the exact class the JF-965-era es evidence and the JF-964 gate exist to guard), for zero verified gain. If a future locale develops a real reporter-grade steal, the de recipe applies to THAT locale (narrowing first, twin only if the collision is live there). The JF-940 closure note's claim '(6) the twin slot in all 17 locale templates' is corrected by this record: the handler fold claims both intent NAMES everywhere, the MODEL layer ships the twin in de+it only. gate-exempt: no code changed; this round corrected a task premise with tool evidence (the generated models) and closed a filed task whose work does not exist.
<!-- SECTION:FINAL_SUMMARY:END -->
