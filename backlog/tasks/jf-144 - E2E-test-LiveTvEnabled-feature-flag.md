---
id: JF-144
title: 'E2E test: LiveTvEnabled feature flag'
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
Create E2E tests that verify the LiveTvEnabled feature flag correctly gates PlayChannelIntent.

When LiveTvEnabled=false, PlayChannelIntent should return a "feature disabled" response.
When LiveTvEnabled=true, PlayChannelIntent should attempt to play the channel.

Approach:
1. Configure plugin with LiveTvEnabled=false
2. Run E2E utterance for PlayChannel (e.g., "metti il canale")
3. Verify response contains "disabled" text
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: PlayChannelIntentHandler.cs, IntentNames.cs (PlayChannel)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests PlayChannel with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response
- [ ] #3 Enabled state attempts channel playback
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created LiveTvFeatureFlagTests.cs. 2 tests: disabled response when LiveTvEnabled=false, normal behavior when enabled. PlayChannelIntentHandler requires ILibraryManager and IUserManager.
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
