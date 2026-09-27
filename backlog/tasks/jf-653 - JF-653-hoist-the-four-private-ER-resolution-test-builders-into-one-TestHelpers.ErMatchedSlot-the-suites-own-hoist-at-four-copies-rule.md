---
id: JF-653
title: >-
  JF-653 - hoist the four private ER-resolution test builders into one
  TestHelpers.ErMatchedSlot (the suite's own hoist-at-four-copies rule)
status: To Do
assignee: []
created_date: '2026-09-27 10:07'
labels:
  - tests
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-642 /simplify round (two reviewers converged on it; the fix touches three test files outside the JF-642 diff, hence a follow-up rather than an in-diff change).

THE FINDING: the test suite now carries FOUR private hand-rolled Alexa.NET Resolution object-graph builders (each ~25 lines of Resolution/Authorities/ResolutionAuthority/ResolutionStatus/ResolutionValueContainer/ResolutionValue nesting, differing only in payload): SetPlaybackSpeedIntentHandlerTests.cs:~78 (RateSlot, Id-keyed), PlayNextEpisodeIntentHandlerTests.cs:~114 (CreatePositionSlot, Id+Name), Unit/PlaybackSpeedTests.cs:~126 (EntitySlot, Id+Name), and PlayByGenreIntentHandlerTests.cs:~373 (CreateIntentRequestWithResolution, Name-only, JF-642). This is the count at which the suite's own house rule (TestHelpers doc comments: TestCandidate 'replaces the four private per-file copies', CreateSong 'hoisted the third private copy', AssertElicitsSlot 'was eight per-file copies') has fired twice before.

THE WORK: one shared builder in TestHelpers, e.g. `TestHelpers.ErMatchedSlot(string name, string? rawValue = null, string? id = null)` returning a Slot with the ER_SUCCESS_MATCH authority graph (the id=null case covers the Name-only shape); migrate all four sites; per-class IntentRequest wrappers stay per-class (each binds different intents/slots). Mechanical, test-only.

VERIFICATION: the four suites stay green byte-identical (no behavior change anywhere); grep shows zero remaining private copies of the authority graph.
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
