---
id: JF-182
title: Add ASR compound-word split mitigation with pairwise-join search fallback
status: Done
assignee: []
created_date: '2026-05-18 18:36'
updated_date: '2026-05-19 18:17'
labels:
  - feature
  - search
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Alexa's ASR splits compound words (e.g., "lazybones" → "lazy bones", "blackjack" → "black jack"). When the split query is sent to Jellyfin's SearchTerm, it returns 0 results because the token sets don't overlap. The fuzzy matcher can't help because there are no candidates to match against.

## Solution
Add a pairwise-adjacent-join search fallback. When Jellyfin's initial SearchTerm returns 0 results, retry with each adjacent word pair joined (N-1 queries for N words), plus the fully-collapsed variant. This catches compound-word splits without false positives for legitimate multi-word titles.

## Guard
Feature is behind a new boolean config setting `AsrCompoundWordFixEnabled` (default: true), exposed in the config UI under Feature Flags.

## Architecture
- New shared utility method `SearchWithAsrFallbackAsync()` in `BaseHandler` that wraps the search-retry logic
- Any handler using `SearchTerm` queries can call it (PlaySongIntentHandler, SearchMediaIntentHandler, PlayArtistSongsIntentHandler, etc.)
- The config check happens inside the utility method — if disabled, it returns the original results immediately

## TDD Approach
Tests written first, then implementation. Verify behavior with feature on AND off.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 New boolean config property AsrCompoundWordFixEnabled in PluginConfiguration with default true
- [ ] #2 Config UI checkbox in Feature Flags section with descriptive label
- [ ] #3 Pairwise-adjacent-join fallback: when initial SearchTerm returns 0 results AND feature enabled, retry with each adjacent word pair joined (N-1 queries)
- [ ] #4 Fully-collapsed fallback: after pairwise attempts, try with all spaces removed (catches single-word compounds)
- [ ] #5 Feature-off path: when AsrCompoundWordFixEnabled=false, search behaves identically to current behavior (no extra queries)
- [ ] #6 Unit tests cover: 0-result initial query triggering fallback, multi-word queries generating correct pairwise variants, feature-off bypassing fallback, single-word queries skipped (nothing to join)
- [ ] #7 Existing tests continue to pass — no regressions
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed via subtasks JF-182.1–182.4: Added AsrVariantGenerator utility for pairwise-adjacent-join search fallback, SearchWithAsrFallbackAsync in BaseHandler, wired into PlaySong/SearchMedia/PlayArtistSong handlers, feature flag in config. 17 new tests across all levels (unit, integration). All 1705 tests passing.
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
