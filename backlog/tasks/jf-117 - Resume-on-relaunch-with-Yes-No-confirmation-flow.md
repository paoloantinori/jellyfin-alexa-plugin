---
id: JF-117
title: Resume-on-relaunch with Yes/No confirmation flow
status: To Do
assignee: []
created_date: '2026-05-12 04:44'
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
- [ ] #1 Skill detects prior playback on re-launch and offers resume confirmation
- [ ] #2 Yes response resumes playback from last known position
- [ ] #3 No response starts a fresh session
- [ ] #4 Confirmation prompt is localized in all 12 locales
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
