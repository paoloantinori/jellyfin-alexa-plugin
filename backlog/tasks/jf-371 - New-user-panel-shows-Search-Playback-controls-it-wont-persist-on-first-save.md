---
id: JF-371
title: New-user panel shows Search/Playback controls it won't persist on first save
status: To Do
assignee: []
created_date: '2026-07-24 19:32'
labels:
  - config
  - ux
  - bug
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a brand-new user is added via the "Add User Skill" modal and saved, the POST body is only `{ Username, InvocationName }` (config.html save loop, the `userId === "undefined"` branch). But the new-user details panel renders the FULL Search/Playback control set (Search Mode, Auto-play, thresholds, Post-Play, Music delivery, announces). So an admin can set e.g. "Post-Play = AutoPlay" on a new user, click Save, and those settings are silently discarded — they must save a second time (now an UPDATE) to persist them.

This PRE-DATES JF-365 (the old createUserRow also built all cells for an unsaved row and the old POST also sent only Username+InvocationName). But it's a real data-loss UX corner worth fixing: either (a) include the per-user settings in the new-user POST payload (requires the backend POST endpoint to accept them), or (b) hide/disable the Search/Playback controls on an unsaved row until the first save creates the user. Option (b) is safer (no backend change) and matches the "add then configure" flow.

Verify the POST endpoint's accepted fields (ConfigurationController CreateUserSkill) before deciding.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
