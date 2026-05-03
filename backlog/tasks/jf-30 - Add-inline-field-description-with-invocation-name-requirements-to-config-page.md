---
id: JF-30
title: Add inline field description with invocation name requirements to config page
status: To Do
assignee: []
created_date: '2026-05-03 10:42'
labels:
  - ux
  - improvement
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

The config page (`config.html`) has an editable invocation name input per user, but no `fieldDescription` or hint explaining the requirements. Users who type a single word like "jellyfin" get a generic "Invalid invocation name" error from the API with no explanation of the 2-word rule.

## What to add

Add a `fieldDescription` div below (or as part of) the invocation name input column explaining:
- Must be at least 2 words (e.g., "jellyfin player", "my media server")
- Must contain only letters, spaces, apostrophes, and periods
- This is what users say to invoke the skill (e.g., "Alexa, ask [invocation name] to play music")

## Files
- `Jellyfin.Plugin.AlexaSkill/Configuration/config.html` (around lines 127-138, the invocation name column)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
