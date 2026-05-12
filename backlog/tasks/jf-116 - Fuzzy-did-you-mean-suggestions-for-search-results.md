---
id: JF-116
title: Fuzzy "did you mean?" suggestions for search results
status: In Progress
assignee:
  - claude
created_date: '2026-05-12 04:44'
updated_date: '2026-05-12 05:37'
labels:
  - enhancement
  - search
  - ux
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When fuzzy matching finds a close but not exact hit, the skill should offer configurable behavior:

**Strategy 1 — "Did you mean?" (Confirm)**: Returns "I didn't find 'Beatls', did you mean 'The Beatles'?" and waits for Yes/No confirmation before playing.

**Strategy 2 — "I'm feeling lucky" (Auto-play)**: Plays the closest match immediately with an announcement: "Playing The Beatles (closest match for 'Beatls')".

The user should be able to choose between these strategies in the Jellyfin plugin configuration page. A new config option (e.g., `FuzzyMatchBehavior` with values `Confirm` or `AutoPlay`) should be exposed in the plugin settings UI (config.html).

Inspired by JellyMusic's fuzzball-based suggestion pattern. Implementation approach: In `BaseHandler.FuzzyMatch()` and disambiguation flow, when the match score is below a confidence threshold but a close candidate exists, check the config setting and either return a confirmation prompt or play the closest match with an announcement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When fuzzy match score is below confidence threshold but a close candidate exists, return 'did you mean X?' prompt
- [ ] #2 Cover song, album, and artist search scenarios
- [ ] #3 Suggestion response is localized in all 12 locales
- [ ] #4 Unit tests for suggestion threshold logic
- [ ] #5 Plugin configuration page exposes a 'Fuzzy match behavior' dropdown with 'Confirm (did you mean?)' and 'Auto-play (feeling lucky)' options
- [ ] #6 Config setting persisted in PluginConfiguration and respected by all fuzzy match code paths
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Architecture
- Add `FuzzyMatchBehavior` enum (Confirm/AutoPlay) to Configuration namespace
- Add `SuggestionThreshold = 40` constant to FuzzyMatcher (below DefaultThreshold=60)
- Add `FindBestMatchWithScore()` to FuzzyMatcher - returns best match with score regardless of threshold
- Add `HandleFuzzyMiss()` to BaseHandler - unified handler for when FuzzyMatch returns null
- Config property defaults to `Confirm`

### Scoring Zones
- Score >= 60: exact match → auto-play (current behavior)
- Score 40-59: suggestion zone → Confirm: "Did you mean X?" / AutoPlay: play with announcement
- Score < 40: truly not found → "not found" message

### Handler Flow Change
Replace in each handler: `FuzzyMatch() → null → DisambiguationHelper.AskFirstMatch()`
With: `FuzzyMatch() → null → HandleFuzzyMiss()` which checks config and acts accordingly.
The Confirm mode reuses DisambiguationHelper session state so YesIntentHandler/NoIntentHandler work unchanged.

### Files to Modify
1. `FuzzyMatcher.cs` - SuggestionThreshold, FindBestMatchWithScore()
2. `PluginConfiguration.cs` - FuzzyMatchBehavior property
3. `BaseHandler.cs` - HandleFuzzyMiss() method
4. 9 intent handlers - replace disambiguation fallback
5. 12 locale JSON files - new strings: FuzzySuggestionPrompt, FuzzySuggestionPromptSsml, FuzzyAutoPlayAnnouncement, FuzzyAutoPlayAnnouncementSsml, FuzzySuggestionReprompt
6. `config.html` - dropdown for fuzzy match behavior

### New Files
1. `Configuration/FuzzyMatchBehavior.cs` - enum
2. Tests in `Unit/FuzzyMatcherTests.cs` - suggestion threshold tests
3. Tests in `Unit/PluginConfigurationTests.cs` - default behavior test
<!-- SECTION:PLAN:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
