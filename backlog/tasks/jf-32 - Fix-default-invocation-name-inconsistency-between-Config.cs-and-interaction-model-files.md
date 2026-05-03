---
id: JF-32
title: >-
  Fix default invocation name inconsistency between Config.cs and interaction
  model files
status: Done
assignee: []
created_date: '2026-05-03 10:42'
updated_date: '2026-05-03 10:57'
labels:
  - consistency
  - documentation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

There's an inconsistency in the default invocation name:
- `Config.cs:16` defines `InvocationName = "jellyfin player"` (2 words)
- The interaction model files (e.g., `model_en-US.json`) have `"invocationName": "jellyfin"` (1 word)

While the interaction model files serve as templates that get overwritten by `SkillInteractionModel.InvocationName` setter at runtime (see `SkillInteractionModel.cs:43`), having the templates say "jellyfin" is confusing and misleading for anyone reading the code. It also means the templates don't pass the same validation that the config page enforces.

## Fix

Update all interaction model JSON files to use `"jellyfin player"` as the invocation name to match `Config.InvocationName`, or add a comment/note in `Config.cs` explaining that the interaction model templates are overwritten at runtime.

## Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json` (all locale files)
- Optionally: `Jellyfin.Plugin.AlexaSkill/Config.cs`
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Resolved by changing default invocation name to "jelly fin" everywhere: Config.cs, config.html (2 locations), all 12 model_*.json files, and ConfigTests.cs. The interaction model templates now match the Config constant.

Also changed test method name from InvocationName_IsJellyfinPlayer to InvocationName_IsJellyFin.
<!-- SECTION:FINAL_SUMMARY:END -->
