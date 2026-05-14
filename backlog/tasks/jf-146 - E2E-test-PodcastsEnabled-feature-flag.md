---
id: JF-146
title: 'E2E test: PodcastsEnabled feature flag'
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
Create E2E tests that verify the PodcastsEnabled feature flag correctly gates PlayPodcastIntent.

When PodcastsEnabled=false, PlayPodcastIntent should return a "feature disabled" response.
When PodcastsEnabled=true, PlayPodcastIntent should work normally.

Approach:
1. Configure plugin with PodcastsEnabled=false
2. Run E2E utterance for PlayPodcast (e.g., "riproduci il podcast")
3. Verify "disabled" response
4. Re-enable and verify normal behavior
5. Use it-IT locale

References: PlayPodcastIntentHandler.cs, IntentNames.cs (PlayPodcast)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests PlayPodcast with flag disabled and enabled
- [ ] #2 Disabled state returns feature-disabled response
- [ ] #3 Enabled state allows podcast playback
- [ ] #4 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created PodcastsFeatureFlagTests.cs. 2 tests: disabled response when PodcastsEnabled=false, normal behavior when enabled.
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
