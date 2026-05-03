---
id: JF-31
title: Add client-side contextual validation for invocation name in config page
status: Done
assignee: []
created_date: '2026-05-03 10:42'
updated_date: '2026-05-03 11:37'
labels:
  - ux
  - improvement
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

The config page only validates invocation names server-side after the user clicks Save. If the name is invalid (e.g., 1 word), the user gets a generic API error with no inline feedback.

## What to add

Add JavaScript validation in the config page (`config.html`) that:
1. Validates on input/blur that the invocation name has at least 2 words
2. Shows an inline error message next to the field when invalid (e.g., "Invocation name must be at least 2 words")
3. Prevents the save action for rows with invalid invocation names
4. Optionally: highlight the field with a visual indicator (red border or similar using Jellyfin's existing styles)

The server-side validation in `ConfigurationController.cs` (lines 69-70, 110) should remain as a safety net.

## Files
- `Jellyfin.Plugin.AlexaSkill/Configuration/config.html` (script section, lines 84-354)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added client-side validation for invocation name in config page: input/blur event listeners validate that the name contains at least 2 words, showing inline error message with yellow border on invalid input. Save handler skips invalid rows with continue to prevent unnecessary API calls.
<!-- SECTION:FINAL_SUMMARY:END -->
