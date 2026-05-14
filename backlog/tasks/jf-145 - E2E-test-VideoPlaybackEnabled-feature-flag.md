---
id: JF-145
title: 'E2E test: VideoPlaybackEnabled feature flag'
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
Create E2E tests that verify the VideoPlaybackEnabled feature flag correctly gates PlayVideoIntent and PlayEpisodeIntent.

When VideoPlaybackEnabled=false:
- PlayVideoIntent should return a "feature disabled" response
- PlayEpisodeIntent should return a "feature disabled" response

When VideoPlaybackEnabled=true, these intents should work normally.

Approach:
1. Configure plugin with VideoPlaybackEnabled=false
2. Run E2E utterances for PlayVideo and PlayEpisode
3. Verify "disabled" response
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: PlayVideoIntentHandler.cs, PlayEpisodeIntentHandler.cs, IntentNames.cs (PlayVideo, PlayEpisode)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests PlayVideo and PlayEpisode with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response for both intents
- [ ] #3 Enabled state allows video/episode playback
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created VideoPlaybackFeatureFlagTests.cs. 4 tests: disabled for PlayVideo and PlayEpisode when VideoPlaybackEnabled=false, normal behavior for both when enabled.
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
