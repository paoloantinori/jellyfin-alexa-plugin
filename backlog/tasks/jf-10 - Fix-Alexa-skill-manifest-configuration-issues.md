---
id: JF-10
title: Fix Alexa skill manifest configuration issues
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:27'
labels: []
milestone: m-1
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Alexa skill manifest (`Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/manifest.json`) has several configuration issues:

1. **Line 19:** `"distributionCountries": []` — empty array means skill not available anywhere
2. **Line 20:** `"isAvailableWorldwide": false` — contradicts the intent of making the skill available
3. **Line 33:** `"testingInstructions": ""` — empty testing instructions
4. **Lines 26-29:** Example phrases reference "Jellyfin Player" but invocation name in models is "jellyfin"
5. **config.html line 136/347:** Hardcoded default invocation name "jellyfin player" should match interaction models

**Fix approach:**
1. Set `isAvailableWorldwide: true` (this is a self-hosted plugin, should work everywhere)
2. Remove empty `distributionCountries` or keep it alongside `isAvailableWorldwide: true`
3. Add meaningful testing instructions
4. Align example phrases with actual invocation name
5. Make invocation name configurable rather than hardcoded
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Skill manifest has isAvailableWorldwide: true
- [ ] #2 Testing instructions populated
- [ ] #3 Example phrases match invocation name
- [ ] #4 Invocation name made configurable in PluginConfiguration
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed manifest.json: set isAvailableWorldwide=true, aligned example phrases with invocation name "jellyfin", added testing instructions. Invocation name configurability deferred — it's already configurable per-user via ConfigurationController.UpdateUserSkill.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
