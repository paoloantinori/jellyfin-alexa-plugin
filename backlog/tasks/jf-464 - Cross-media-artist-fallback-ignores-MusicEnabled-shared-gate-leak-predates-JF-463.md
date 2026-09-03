---
id: JF-464
title: >-
  Cross-media artist fallback ignores MusicEnabled (shared-gate leak, predates
  JF-463)
status: In Progress
assignee: []
created_date: '2026-09-03 07:51'
updated_date: '2026-09-03 08:21'
labels: []
dependencies: []
references:
  - JF-463 review finding
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
    (TryEntityFallbackAsync)
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlayByGenreFallbackTests.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-463 code-review pass (2026-09-03): the shared cross-media artist fallback path (BaseHandler.TryEntityFallbackAsync -> ArtistSearch -> BuildArtistSongsResponseAsync) does not consult the MusicEnabled feature flag. If music is disabled for a user, a genre miss (PlayByGenre, JF-463) or mood miss (PlayMoodMusic) can still play artist songs, because the fallback's artist queries skip FilterByContentAccess entirely. The exposure predates JF-463 (PlayMoodMusic has had it since the TryEntityFallbackAsync consolidation) and is identical in shape for both callers.

The fix belongs in the SHARED gate (TryEntityFallbackAsync), not in any caller's wiring: short-circuit to null when the effective music feature flag is off for the user (follow the IfFeatureDisabled / GetSearchResponseMode per-user resolution pattern in BaseHandler), so every current and future caller inherits the behavior. Before implementing, verify what MusicEnabled actually gates today (grep IfFeatureDisabled usages and the FeatureFlagTests file per flag) to confirm the fallback is the only leak or find the others.

Acceptance criteria:
- With music disabled for the user, a genre miss and a mood miss both fall through to their own not-found (no AudioPlayer directive, no artist query issued; assert via recorded queries).
- With music enabled, the fallback behaves exactly as today (existing JF-463 and PlayMoodMusic tests unchanged and green).
- Unit test file following PlayByGenreFallbackTests conventions, one test per caller plus a shared-gate test.
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
