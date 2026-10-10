---
id: JF-862
title: >-
  The named-item resolve chain converges at the third caller: hoist the
  SearchTerm+Fuzzy+arbitration helper on SearchService with the multi-match
  policy as a parameter
status: To Do
assignee: []
created_date: '2026-10-10 22:39'
labels:
  - refactor
  - tech-debt
  - search
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-11 by the orchestrator from the JF-329 completion round (its /simplify reasoned-skip finding, the extraction-on-convergence rule now fired).

ResolveAlbumAsync in ShowPhotoAlbumIntentHandler is the THIRD inline SearchTerm-then-SearchItemsFuzzyAsync-then-FuzzyMatch-arbitration chain in the codebase (beside PlayBook's book leg and TvNextUp.ResolveSeriesAsync, which already diverges first-match-wins). Hoist a shared resolve-named-item helper on SearchService with the per-site multi-match policy as a parameter (arbitration-with-prompt vs top-ranked-fallback vs first-match-wins), so the three sites cannot drift and the fourth caller lands on the helper. Read all three sites first; the policies genuinely differ and the parameter shape must express that without collapsing the distinctions.
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
