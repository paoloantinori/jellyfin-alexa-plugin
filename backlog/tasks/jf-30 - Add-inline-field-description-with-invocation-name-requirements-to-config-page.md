---
id: JF-30
title: Add inline field description with invocation name requirements to config page
status: Done
assignee: []
created_date: '2026-05-03 10:42'
updated_date: '2026-05-03 11:35'
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
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added fieldDescription div below invocation name input in config page with guidance: "Two or more words (e.g. jelly fin). This is what you say to invoke the skill." Also fixed copy-paste bug where input name was "LwaClientSecret" instead of "InvocationName".
<!-- SECTION:FINAL_SUMMARY:END -->
