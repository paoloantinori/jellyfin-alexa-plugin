---
id: JF-117
title: Resume-on-relaunch with Yes/No confirmation flow
status: Done
assignee:
  - '@claude'
created_date: '2026-05-12 04:44'
updated_date: '2026-05-12 13:55'
labels:
  - enhancement
  - playback
  - ux
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the skill is re-launched while audio playback was previously active, detect the prior session state and ask the user "You were listening to {track}. Would you like to resume?" with Yes/No handling. This creates a better re-engagement UX compared to starting fresh each time.

Inspired by AskPlex's LaunchRequestHandler which detects active playback state via persistent storage and offers resume confirmation.

Implementation: Track last-played item + position in session/user state. On `LaunchRequest`, check for active/recent playback and trigger confirmation dialog.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Skill detects prior playback on re-launch and offers resume confirmation
- [x] #2 Yes response resumes playback from last known position
- [x] #3 No response starts a fresh session
- [x] #4 Confirmation prompt is localized in all 12 locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
LaunchRequestHandler detects prior audio via context.AudioPlayer and offers resume prompt. YesIntent resumes from stored offset, NoIntent starts fresh. ResumeHelper manages state as proper DTO in session attributes. 26 unit tests. All 17 locales updated. 1221 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
- [x] #2 dotnet build passes with 0 errors
- [x] #3 dotnet test passes
- [x] #4 No new compiler warnings introduced
- [x] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [x] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
