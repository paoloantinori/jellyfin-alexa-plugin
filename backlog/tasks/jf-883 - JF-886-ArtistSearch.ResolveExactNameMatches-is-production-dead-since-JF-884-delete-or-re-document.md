---
id: JF-883
title: >-
  JF-886: ArtistSearch.ResolveExactNameMatches is production-dead since JF-884 -
  delete or re-document
status: To Do
assignee: []
created_date: '2026-10-13 21:04'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review F6 from JF-885 (2026-10-13): ArtistSearch.ResolveExactNameMatches is production-dead since JF-884 (the gate now resolves through ResolveErCandidates, which owns its exact layer inline) and its doc comment still presents it as a live production path, which misleads. Small cleanup: either delete the method and its tests, or repoint its doc at ResolveErCandidates as the caller-owned layer; keep the suite green both TFMs; quick /simplify + /code-review pass suffices for the class.
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
