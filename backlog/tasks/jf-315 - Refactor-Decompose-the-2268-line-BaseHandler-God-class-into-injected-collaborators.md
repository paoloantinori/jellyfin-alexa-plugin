---
id: JF-315
title: >-
  Refactor: Decompose the 2268-line BaseHandler God class into injected
  collaborators
status: To Do
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-07-13 20:17'
labels:
  - refactor
  - maintainability
  - tech-debt
milestone: m-7
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`Alexa/Handler/BaseHandler.cs` is 2268 lines with 60+ protected members spanning response building, fuzzy search, ASR fallback, APL directives, playback progress reporting, radio, playlists, resume-index math, XML/SSML escaping, and URL building. All 61 handlers inherit ALL of it. This is the single biggest maintainability risk in the codebase (architecture review 2026-07-12): any change ripples to every handler and the class is impossible to reason about in isolation.

This is strategic debt, not a bug — scope it as a deliberate, incremental refactor. Extract cohesive collaborators (e.g. ResponseFactory, SearchService, ProgressReporter, UrlBuilder, SsmlBuilder) and inject them, migrating handlers in batches while keeping the test suite green. Do NOT attempt in one big-bang change. Consider doing this as a parent task with per-collaborator subtasks. The large existing test suite (159 test files, ~2360+ cases) is the safety net that makes this feasible.

Related: BaseHandler currently forces the singleton stateless-by-luck constraint on every handler (see the concurrency milestone) — extracting state-free collaborators reduces that surface too.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A target decomposition is documented (which responsibilities move to which collaborator) before code changes
- [ ] #2 At least the response-building and search responsibilities are extracted into separately-testable, injected services
- [ ] #3 BaseHandler line count is materially reduced and no longer mixes unrelated responsibilities
- [ ] #4 All existing unit tests pass after each extraction batch (no behavior change)
- [ ] #5 New collaborators have direct unit tests
- [ ] #6 Handlers consume collaborators via constructor injection (readonly fields)
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
