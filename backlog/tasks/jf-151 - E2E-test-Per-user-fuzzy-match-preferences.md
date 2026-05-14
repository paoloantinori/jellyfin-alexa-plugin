---
id: JF-151
title: 'E2E test: Per-user fuzzy match preferences'
status: Done
assignee: []
created_date: '2026-05-14 12:28'
updated_date: '2026-05-14 13:52'
labels:
  - testing
  - e2e
  - configuration
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create E2E tests that verify per-user fuzzy match configuration (FuzzyMatchBehavior, FuzzyMatchThreshold) correctly affects search behavior.

Configuration options:
- FuzzyMatchBehavior: Confirm (ask user to confirm ambiguous match) vs AutoPlay (play best match directly)
- FuzzyMatchThreshold: 0-100 (minimum score for a match)

Test scenarios:
1. With FuzzyMatchBehavior=Confirm, a partial match should trigger disambiguation prompt
2. With FuzzyMatchBehavior=AutoPlay, a partial match should auto-play
3. With high FuzzyMatchThreshold, poor matches should trigger "not found" response
4. With low FuzzyMatchThreshold, poor matches should still resolve

Approach:
1. Configure per-user fuzzy match settings
2. Run E2E utterances with intentionally imprecise search terms
3. Verify response matches expected behavior (disambiguation, auto-play, or not found)
4. Use it-IT locale

References: BaseHandler.cs (FuzzyMatch, HandleFuzzyMiss), FuzzyMatcher.cs, User.cs
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests fuzzy match with Confirm behavior
- [ ] #2 E2E fixture tests fuzzy match with AutoPlay behavior
- [ ] #3 Disambiguation prompt triggered when Confirm and partial match
- [ ] #4 Auto-play triggered when AutoPlay and partial match
- [ ] #5 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created FuzzyMatchConfigurationTests.cs with 19 tests. Covers per-user FuzzyMatchThreshold (6), HandleFuzzyMiss behavior Confirm vs AutoPlay (6), default values (3), and FuzzyMatcher API (4). Tests verify disambiguation prompts, auto-play responses, and threshold sensitivity.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
