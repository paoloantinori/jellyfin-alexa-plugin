---
id: JF-159
title: Fix "closest match" announcement for high-confidence fuzzy matches
status: Done
assignee: []
created_date: '2026-05-16 14:31'
updated_date: '2026-05-16 14:53'
labels:
  - bug
  - ux
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**User feedback**: "If I ask Jellyfin player to play song About Today she plays it but says 'playing About Today, closest match to About Today.'"

**Root cause**: `HandleFuzzyMiss` in `BaseHandler.cs:726-741` always uses `FuzzyAutoPlayAnnouncement` ("closest match for X") whenever `autoAccept` is true, even when the fuzzy score is 90-100 (near-exact or exact match). When multiple songs with the same name exist (different artists), the code enters `HandleFuzzyMiss`, picks the best one, and unnecessarily announces "closest match".

**Fix**: In `HandleFuzzyMiss`, when the score is >= 90 (contains match or near-exact), use the normal play announcement instead of `FuzzyAutoPlayAnnouncement`. The score is already available as `bestWithScore.Value.Score` at line 718. Only use "closest match" language for borderline matches (score 40-89).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Exact match (score 100) plays without 'closest match' qualifier
- [ ] #2 Contains match (score 90) plays without 'closest match' qualifier
- [ ] #3 Borderline matches (score 40-89) still show 'closest match' announcement
- [ ] #4 Existing fuzzy match tests updated to verify new behavior
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan — JF-159

### Problem
`HandleFuzzyMiss` (BaseHandler.cs:726-741) always uses `FuzzyAutoPlayAnnouncement` when `autoAccept` is true, even for score 90-100 matches where "closest match" is misleading.

### Approach
Add a score-based branch: high-confidence matches (>= 90) return the play response as-is (no output speech override, just like the normal single-result path). Borderline matches (40-89) keep the current "closest match" announcement.

### Steps

1. **Edit `BaseHandler.cs:726-741`** — In the `autoAccept` block, add a condition:
   - If `score >= 90`: return `(FuzzyMissOutcome.SuggestionHandled, playResponse)` directly — the `autoPlayFunc` already built the response, and Alexa will just start playback without the "closest match" speech.
   - If `score < 90`: keep current behavior (set `FuzzyAutoPlayAnnouncement` as OutputSpeech).

2. **Update `FuzzyMatchAutoAcceptTests.cs`** — Add test cases:
   - `HighScore_ExactMatch_NoAnnouncementOverride` — score 100, verify OutputSpeech is NOT `FuzzyAutoPlayAnnouncement`.
   - `HighScore_ContainsMatch_NoAnnouncementOverride` — score 90, same check.
   - `BorderlineScore_HasClosestMatchAnnouncement` — score 70, verify OutputSpeech IS `FuzzyAutoPlayAnnouncement`.

3. **Verify existing tests pass** — Run `dotnet test` to confirm no regressions.

### Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` (lines 726-741)
- `Jellyfin.Plugin.AlexaSkill.Tests/Handler/FuzzyMatchAutoAcceptTests.cs`
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed HandleFuzzyMiss in BaseHandler.cs to skip "closest match" announcement when fuzzy score >= 90. Near-exact/exact matches now play directly without the redundant qualifier. Borderline matches (score < 90) still get the announcement. Added 3 new tests: Score100_DoesNotOverrideOutputSpeech, Score90_DoesNotOverrideOutputSpeech, ScoreBelow90_ProducesClosestMatchAnnouncement. All 1471 tests pass.
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
