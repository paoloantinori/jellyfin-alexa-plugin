---
id: JF-147
title: 'E2E test: SleepTimerEnabled feature flag'
status: Done
assignee: []
created_date: '2026-05-14 12:28'
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
Create E2E tests that verify the SleepTimerEnabled feature flag correctly gates SleepTimerIntent.

When SleepTimerEnabled=false, SleepTimerIntent should return a "feature disabled" response.
When SleepTimerEnabled=true, SleepTimerIntent should start a sleep timer.

Approach:
1. Configure plugin with SleepTimerEnabled=false
2. Run E2E utterance for SleepTimer (e.g., "imposta un timer di 30 minuti")
3. Verify "disabled" response
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: SleepTimerIntentHandler.cs, IntentNames.cs (SleepTimer)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests SleepTimer with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response
- [ ] #3 Enabled state starts sleep timer
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created SleepTimerFeatureFlagTests.cs. 2 tests: disabled response when SleepTimerEnabled=false, normal behavior when enabled. SleepTimerIntentHandler only needs ISessionManager, PluginConfiguration, ILoggerFactory.
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
