---
id: JF-148
title: 'E2E test: RadioModeEnabled feature flag'
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
Create E2E tests that verify the RadioModeEnabled feature flag correctly gates radio-related intents.

When RadioModeEnabled=false:
- PlayRadioIntent should return a "feature disabled" response
- TurnRadioOnIntent should return a "feature disabled" response
- TurnRadioOffIntent should return a "feature disabled" response

When RadioModeEnabled=true, these intents should work normally.

Note: Unit tests already exist for PlayRadio with this flag. This task adds E2E coverage to verify the full Alexa pipeline respects the flag.

Approach:
1. Configure plugin with RadioModeEnabled=false
2. Run E2E utterances for PlayRadio, TurnRadioOn, TurnRadioOff
3. Verify "disabled" response for each
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: PlayRadioIntentHandler.cs, TurnRadioOnIntentHandler.cs, TurnRadioOffIntentHandler.cs, IntentNames.cs
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests PlayRadio, TurnRadioOn, TurnRadioOff with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response for all three intents
- [ ] #3 Enabled state allows radio functionality
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created RadioModeFeatureFlagTests.cs. 6 tests: disabled for PlayRadio, TurnRadioOn, TurnRadioOff when RadioModeEnabled=false, normal behavior for all three when enabled. TurnRadioOn/Off only need base handler deps (no ILibraryManager/IUserManager).
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
