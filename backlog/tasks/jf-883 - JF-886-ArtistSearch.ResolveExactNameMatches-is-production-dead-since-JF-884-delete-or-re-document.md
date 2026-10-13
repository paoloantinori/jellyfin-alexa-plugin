---
id: JF-883
title: >-
  JF-886: ArtistSearch.ResolveExactNameMatches is production-dead since JF-884 -
  delete or re-document
status: Done
assignee: []
created_date: '2026-10-13 21:04'
updated_date: '2026-10-13 22:16'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf886 (4b577144), merged to main cc2b8cda, pushed. Deadness VERIFIED (zero production callers repo-wide; the single pre-JF-884 caller replaced by ResolveErCandidates in 6f88e895 which owns the identical exact layer inline; IsExactNameMatch live at 4 sites and kept). The method and its 14-line doc deleted; the dangling cref repointed; the 5 dead-method tests folded into 4 ResolveErCandidates pins (dedupe-direction and blank-entry pins strengthened to both layers; the order pin restored after the review caught the fold losing its discriminating power). Red proofs: each sabotage flipped exactly its pin on both TFMs. Gates: /simplify (3 agents, 2 reasoned skips) + /code-review high (3 applied). Tests 5941/5941 BOTH TFMs; Release 0/0. Pure deletion in production; rides the next deploy batch.
<!-- SECTION:FINAL_SUMMARY:END -->
