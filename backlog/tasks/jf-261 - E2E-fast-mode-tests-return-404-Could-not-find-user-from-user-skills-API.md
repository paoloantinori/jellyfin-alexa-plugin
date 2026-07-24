---
id: JF-261
title: 'E2E: fast-mode tests return 404 "Could not find user" from user-skills API'
status: Done
assignee: []
created_date: '2026-06-05 16:26'
updated_date: '2026-06-05 16:46'
labels:
  - e2e
  - bug
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**2 failing E2E tests** — both in fast-mode:

1. `fast-metti una canzone dei soul cou` → `PATCH /alexaskill/api/user-skills/{userId} returned 404: {"error":"Could not find user"}`
2. `fast-metti una canzone dei xyzzyfoo` → same 404 error

The test tries to set SearchResponseMode=Fast via `PATCH /alexaskill/api/user-skills/{userId}` but gets a 404. This suggests the user-skills endpoint doesn't recognize the Jellyfin user ID, possibly because:
- The user ID format changed in a recent migration
- The endpoint expects a different identifier (Alexa user ID vs Jellyfin user ID)
- The plugin config was partially wiped during deploy

**Fixture**: `tests/integration/fixtures/e2e_it-IT.yaml` (fast_mode section)
**Root cause area**: `Controller/` — user-skills API endpoint user resolution
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed user-skills API 404 by auto-provisioning plugin users on PATCH. When `PATCH /alexaskill/api/user-skills/{userId}` is called with a valid Jellyfin user GUID that has no plugin config entry, the endpoint now validates the GUID against the Jellyfin user manager and creates a new plugin User before applying the update. Only `UpdateUserSkill` auto-provisions — `DeleteUserSkill` and `GetUserSkillAuthorisation` still require an existing plugin user. Added 7 new unit tests covering auto-provision, multi-field update, invalid user, defaults, and no-re-provision scenarios. Consolidated duplicate test helpers.
<!-- SECTION:FINAL_SUMMARY:END -->
