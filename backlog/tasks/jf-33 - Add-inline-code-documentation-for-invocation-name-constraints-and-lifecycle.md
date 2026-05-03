---
id: JF-33
title: Add inline code documentation for invocation name constraints and lifecycle
status: Done
assignee: []
created_date: '2026-05-03 10:42'
updated_date: '2026-05-03 11:39'
labels:
  - documentation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

The invocation name system has non-obvious constraints and a specific lifecycle, but there's minimal inline documentation. A developer reading the code would not immediately understand:
- Why 2 words are required (Amazon's rule)
- That the interaction model templates get overwritten at runtime
- How the invocation name flows from config → SkillInteractionModel → SMAPI → Alexa cloud
- That each user gets their own skill with their own invocation name

## What to add

Add concise XML doc comments in key locations:
1. `Config.cs` - on `InvocationName` constant: note it's the default, must be 2+ words per Amazon rules
2. `SkillInteractionModel.cs` - on the `InvocationName` property setter: note it overwrites the template's invocation name
3. `ConfigurationController.cs` - on the validation logic: note the 2-word requirement comes from Amazon's invocation name rules
4. `Plugin.cs` - on `BuildSkillInteractionModels()`: note the invocation name is per-user

Keep comments short and focused on WHY, not WHAT.

## Files
- `Jellyfin.Plugin.AlexaSkill/Config.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/SkillInteractionModel.cs`
- `Jellyfin.Plugin.AlexaSkill/Controller/ConfigurationController.cs`
- `Jellyfin.Plugin.AlexaSkill/Plugin.cs`
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added concise XML doc comments in 4 files: Config.cs (Amazon 2-word rule, per-user override), SkillInteractionModel.cs (overwrites template), ConfigurationController.cs (validation rationale), Plugin.cs (per-user invocation name in BuildSkillInteractionModels).
<!-- SECTION:FINAL_SUMMARY:END -->
