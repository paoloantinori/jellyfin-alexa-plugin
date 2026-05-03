---
id: JF-29
title: Fix CreateNewUserSkill ignoring user-provided invocation name
status: To Do
assignee: []
created_date: '2026-05-03 10:42'
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
- [ ] #1 /simplify
<!-- DOD:END -->
