---
id: JF-876
title: >-
  JF-877: movie multi-value ER autoplay - two movies sharing a first word
  silently pick rank-1 (extend MultiValueErDisambiguation to the movie path)
status: To Do
assignee: []
created_date: '2026-10-12 21:12'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual 1 from JF-874 (worker report, 2026-10-12 night): the movie path reads GetCanonicalValue (rank-1 only), and PartialNameSynonyms now widens to Movie, so two movies sharing a first substantive word are both ER-reachable ("Alien" vs "Alien: Romulus"): Amazon's rank-1 silently picks one and it auto-plays through the exact-name path. The artist path already solved this class: MultiValueErDisambiguation (JF-690/JF-702) reads every ER canonical and fires the yes/no cycling on >=2 distinct library items. Extend the movie leg the same way when the household grows same-first-word movies (the 12-title seed today has no collision, which is why this is deferred: no live case exists).
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
