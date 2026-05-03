---
id: JF-29
title: Fix CreateNewUserSkill ignoring user-provided invocation name
status: Done
assignee: []
created_date: '2026-05-03 10:42'
updated_date: '2026-05-03 11:34'
labels:
  - bug
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Bug

In `ConfigurationController.cs:121-123`, the `CreateNewUserSkill` endpoint validates the `invocationName` from the request body (lines 106-113) but never assigns it to the `UserSkill`. Instead, it always uses the hardcoded default:

```csharp
UserSkill userSkill = new UserSkill
{
    InvocationName = Config.InvocationName,  // always "jellyfin player"
    UserSkillStatus = UserSkillStatus.LwaAuthPending
};
```

**Fix**: Change to use the validated `invocationName` variable from the request body.

## Files
- `Jellyfin.Plugin.AlexaSkill/Controller/ConfigurationController.cs` (line 123)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed ConfigurationController.CreateNewUserSkill to use the validated invocation name from the request body instead of the hardcoded default. Extracted IsValidInvocationName() helper to deduplicate validation between CreateNewUserSkill and UpdateUserSkill. Replaced Split() allocation with Contains() for efficiency. Added regression test.
<!-- SECTION:FINAL_SUMMARY:END -->
