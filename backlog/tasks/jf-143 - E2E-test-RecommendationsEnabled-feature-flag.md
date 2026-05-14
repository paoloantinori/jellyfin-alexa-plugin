---
id: JF-143
title: 'E2E test: RecommendationsEnabled feature flag'
status: Done
assignee: []
created_date: '2026-05-14 12:27'
updated_date: '2026-05-14 13:12'
labels:
  - testing
  - e2e
  - configuration
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create E2E tests that verify the RecommendationsEnabled feature flag correctly gates the RecommendIntent.

When RecommendationsEnabled=false, RecommendIntent should return a "feature disabled" response.
When RecommendationsEnabled=true, RecommendIntent should return recommendation results.

Approach:
1. Configure plugin with RecommendationsEnabled=false
2. Run E2E utterance for Recommend (e.g., "consigliami della musica")
3. Verify response contains "disabled" text
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: RecommendIntentHandler.cs, IntentNames.cs (Recommend)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests Recommend with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response
- [ ] #3 Enabled state returns recommendation results
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created RecommendationsFeatureFlagTests.cs. 2 tests: disabled response when RecommendationsEnabled=false, normal behavior when enabled. RecommendIntentHandler is in namespace Handler (not Handler.Intent) and requires IUserDataManager.
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
