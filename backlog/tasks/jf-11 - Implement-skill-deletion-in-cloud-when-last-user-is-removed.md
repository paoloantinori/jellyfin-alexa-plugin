---
id: JF-11
title: Implement skill deletion in cloud when last user is removed
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-30 15:05'
labels: []
milestone: m-1
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`ConfigurationController.cs:164` has a TODO: "Delete skill in the cloud when there are no other users with the same skill"

When a user removes their skill configuration, the plugin should check if any other users are still using the same Alexa skill. If the removing user is the last one, the skill should be deleted from the Amazon developer console via SMAPI.

**Implementation:**
1. In the user deletion/removal flow, count remaining users with active skill configurations
2. If count reaches 0, call SmapiManagement.DeleteSkill()
3. Log the deletion
4. Handle deletion failure gracefully (log warning, don't block user removal)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Cloud skill deletion triggered when last user is removed
- [ ] #2 Skill deletion failure doesn't block user removal
- [ ] #3 Operation logged with appropriate detail
- [ ] #4 Unit tests for last-user and multi-user scenarios
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented cloud skill deletion in ConfigurationController.DeleteUserSkill. When a user is removed, the system checks if any other users share the same SkillId. If the removed user was the last one, the skill is deleted from the Amazon developer console via SmapiManagement.DeleteSkill(). Deletion failure is caught, logged, and does not block local user removal. Added 5 unit tests covering: last user, shared skill, different skill IDs, null skill, and no skill scenarios.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
