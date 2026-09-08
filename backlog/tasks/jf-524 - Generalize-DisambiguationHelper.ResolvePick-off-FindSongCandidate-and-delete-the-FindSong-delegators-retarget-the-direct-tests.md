---
id: JF-524
title: >-
  Generalize DisambiguationHelper.ResolvePick off FindSongCandidate and delete
  the FindSong delegators (retarget the direct tests)
status: To Do
assignee: []
created_date: '2026-09-08 12:23'
labels:
  - refactor
  - tech-debt
  - multi-turn
dependencies: []
references:
  - JF-407
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-407 item 2 simplify + review passes (2026-09-08). The pick-words machinery now lives in DisambiguationHelper, but two artifacts of the zero-churn move remain:

(1) ResolvePick is typed List<FindSongCandidate> (a Handler.Intent DTO) while it advertises itself as the numbered-candidate resolver shared by every DisambiguationHelper-based picker; the next picker with a different candidate record cannot call it. Generalize to candidate names (IReadOnlyList<string>) or a selector Func, keeping the tables internal. Then delete the thin delegators in FindSongIntentHandler (~660-663) and retarget the ~20 direct ResolvePick tests to the shared helper (the delegators were kept ONLY to avoid test churn in the zero-behavior move; both review passes flag them as the artifact to remove in the same change that retargets the tests).

(2) Cosmetic rider: the section rationale comment in DisambiguationHelper was consolidated under the banner in JF-407; keep future additions there.

Constraint: pure refactoring, no behavior change; the JF-395 negative-exit ordering (single-token negative before ResolvePick, multi-token after) is pinned by existing tests and must not move.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ResolvePick generalized off FindSongCandidate (IReadOnlyList<string> candidateNames or a Func<T,int,string> selector) so DisambiguationHelper's other pickers (album/artist disambiguation flows) can use it; FindSongCandidate stays in FindSongSessionData.cs
- [ ] #2 The three thin delegators in FindSongIntentHandler (lines ~660-663) deleted and the ~20 direct ResolvePick tests in FindSongIntentHandlerTests retargeted to DisambiguationHelper (the delegators exist only to avoid test churn)
- [ ] #3 IsNegativeAnswer consumers evaluated: any picker that needs the JF-395 negative-exit gets it
- [ ] #4 Full suite green; /simplify + code-review high gates before merge
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
