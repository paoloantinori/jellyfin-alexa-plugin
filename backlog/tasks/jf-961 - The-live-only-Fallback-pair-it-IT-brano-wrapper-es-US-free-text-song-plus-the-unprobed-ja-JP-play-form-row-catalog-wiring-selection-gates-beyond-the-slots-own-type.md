---
id: JF-961
title: >-
  The live-only Fallback pair (it-IT brano wrapper, es-US free-text song) plus
  the unprobed ja-JP play-form row: catalog-wiring selection gates beyond the
  slot's own type
status: To Do
assignee: []
created_date: '2026-11-25 10:56'
labels:
  - nlu
  - catalog-gate
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-959 re-verification census (claudedocs/census_jf959_reverify_2026-11-25.md, 2026-11-25, 602 probes with green evergreen controls): two still-red rows are PROVEN live-only (lab GREEN x2, live Fallback x2, identical template content verified on the live model), making catalog wiring the only structural difference. (1) it-IT 'chiedi a mia collezione di suonare il brano rapsodia': the armed SongTitle catalog gates intent SELECTION on the composed wrapper shape (the JF-684 catalog-gate x wrapper interaction); the JF-945 title-less opener fixed the podcast analog, so the candidate fix is 'Di suonare il brano' (and per the JF-844 lesson each verb needs its own literal row: Di suonare/Di riprodurre/Di mettere il brano) — with the row-count cost weighed against the wrapper's user value. (2) es-US 'Reproduce la cancion luciernagas del sur': Fallback live-only on a FREE-text MusicRecording slot with catalog wiring present on OTHER types (SeriesName/AudiobookTitle/JellyfinArtist verified wired); the mechanism by which other types' wiring gates a free-text slot's selection needs investigation (candidate: the JF-684 selection-discouragement is model-wide when any catalog-backed type carries no match for its slot in the utterance). Also fold the ja-JP note: the census row (kana play form) was NOT probed in the re-verification (the worker probed the shuffle form, green); the play-form row needs its own re-probe before any fixture decision.
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
