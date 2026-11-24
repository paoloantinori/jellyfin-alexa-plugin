---
id: JF-953
title: >-
  es wrapper known limitations: skip direction-only, skip amount-first, reminder
  steal (structural, confirmed by lab + ablation, needs real user testing for
  any future fix)
status: To Do
assignee: []
created_date: '2026-11-23 20:48'
labels:
  - nlu
  - es
  - known-limitation
dependencies: []
references:
  - scripts/nlu-lab/runs/20261123-211417-es-red-probe.json
  - scripts/nlu-lab/runs/20261123-213154-es-red-fix1.json
  - scripts/nlu-lab/runs/20261123-214312-es-ablation.json
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-11-23 NLU fix round (the session after the 1.1.2.0 release). Three es wrapper families are systematically broken, confirmed by lab rounds AND resistant to template fixes:

(1) SKIP DIRECTION-ONLY WRAPPER: "pide a mi coleccion que salte adelante/hacia atras" (bare direction, no duration) falls to Fallback on the LAB skill (no catalog), meaning the template itself lacks trainer weight for these short shapes. Template rows exist ("que salte {seek_direction}") but don't anchor. Added more context variants ("pide que salte {seek_direction}") - didn't help. Root cause: "que salte adelante" is too short/generic for the trainer to distinguish. The WITH-duration forms work fine.

(2) SKIP AMOUNT-FIRST WRAPPER: "pide a mi coleccion que salte 30 segundos adelante" (amount before direction) falls to Fallback on the LAB. Template row exists ("que salte {seek_amount} {seek_unit} {seek_direction}") but doesn't anchor. Root cause: amount-first word order is less natural in Spanish and the trainer prefers direction-first.

(3) REMINDER: "recuerdame en 30 minutos" routes to PlaySongIntent on the LAB (no catalog). Template row "recuerdame en {reminder_duration}" exists. Added more context rows and wrapper tails - didn't help. ABLATION TEST (removing 5 greedy PlaySong carriers: Dame/Pon/Toca/Inicia/Suelta {song}): the reminder did NOT recover (went to SleepTimer instead), and song queries broke CATASTROPHICALLY (ALL forms went to PlayArtistSongsIntent, including "escucha" and "quiero escuchar" which were NOT touched). The greedy carriers are LOAD-BEARING: they balance the competition, and removing them breaks working song queries.

CONCLUSION: these three families are structural limitations of the current model architecture, not fixable with more template rows or carrier removal. Accepting as known limitations. Any future work requires real user testing (maintainer requirement: at least one user must be involved in any carrier decision). Known workarounds: skip with duration works; "pon un recordatorio" works for reminders.
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
