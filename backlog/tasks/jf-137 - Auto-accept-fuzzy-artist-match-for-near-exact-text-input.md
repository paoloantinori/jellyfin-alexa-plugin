---
id: JF-137
title: Auto-accept fuzzy artist match for near-exact text input
status: Done
assignee: []
created_date: '2026-05-12 16:22'
updated_date: '2026-05-12 17:38'
labels:
  - fuzzy-matching
  - ux
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When typing "soul coughing" (lowercase) via the Alexa developer console text simulator, the fuzzy matcher asks "Non ho trovato 'soul coughing'. Intendevi Soul Coughing?" instead of auto-accepting.

## Root Cause Analysis

The flow in `PlayArtistSongsIntentHandler`:
1. Jellyfin `SearchTerm = "soul coughing"` returns artists including "Soul Coughing" → `artists.Count > 1`
2. `HandleFuzzyMiss` is called (BaseHandler.cs:614-672)
3. `FuzzyMatcher.FindBestMatchWithScore` normalizes both to lowercase → `"soul coughing"` vs `"soul coughing"` → **score = 100** (exact case-insensitive match via `PartialRatio` line 118)
4. Score 100 >= `SuggestionThreshold` (40) → passes
5. **Line 636**: `_config.FuzzyMatchBehavior == FuzzyMatchBehavior.AutoPlay` — **default is `Confirm`**
6. Falls through to "Did you mean?" confirmation prompt (line 646-671)

**The bug**: `HandleFuzzyMiss` has no short-circuit for high-confidence/exact matches. A score of 100 (case-insensitive exact match) should auto-accept regardless of `FuzzyMatchBehavior`. The `FuzzyMatchBehavior` config should only govern borderline matches (score 40-60), not exact ones.

## Relevant Code

- `BaseHandler.cs:614-672` — `HandleFuzzyMiss` method
- `BaseHandler.cs:636` — `FuzzyMatchBehavior` check (no score threshold)
- `FuzzyMatcher.cs:118` — `PartialRatio` returns 100 for exact case-insensitive match
- `FuzzyMatcher.cs:16` — `DefaultThreshold = 60`
- `FuzzyMatcher.cs:23` — `SuggestionThreshold = 40`
- `PluginConfiguration.cs` — `FuzzyMatchBehavior` defaults to `Confirm`

## Suggested Fix

Add a confidence tier in `HandleFuzzyMiss`: if score >= `DefaultThreshold` (60), auto-accept regardless of `FuzzyMatchBehavior`. Only consult `FuzzyMatchBehavior` for scores between `SuggestionThreshold` (40) and `DefaultThreshold` (60) — the "borderline" zone. This preserves the config's purpose (controlling uncertain matches) while never asking for confirmation on near-certain matches.

**Repro**: Alexa developer console → text input → "chiedi a jellyfin player di mettere una canzone dei soul coughing"
**Expected**: Auto-plays Soul Coughing songs
**Actual**: "Non ho trovato 'soul coughing'. Intendevi Soul Coughing?"
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Exact case-insensitive match (score 100) auto-accepts without confirmation regardless of FuzzyMatchBehavior setting
- [ ] #2 Score >= DefaultThreshold (60) auto-accepts regardless of FuzzyMatchBehavior
- [ ] #3 Score between SuggestionThreshold (40) and DefaultThreshold (60) respects FuzzyMatchBehavior config (Confirm vs AutoPlay)
- [ ] #4 Score < SuggestionThreshold (40) returns NotFound as before
- [ ] #5 Existing FuzzyMatchBehavior=Confirm behavior preserved for borderline matches
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Fixed: HandleFuzzyMiss now auto-accepts when score >= DefaultThreshold (60) regardless of FuzzyMatchBehavior. Only borderline matches (score 40-60) consult the Confirm/AutoPlay config. 7 unit tests cover all tiers: high-score auto-accept, borderline respects config, low-score returns NotFound.
<!-- SECTION:FINAL_SUMMARY:END -->
