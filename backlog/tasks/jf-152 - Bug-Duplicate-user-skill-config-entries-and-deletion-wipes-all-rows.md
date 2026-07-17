---
id: JF-152
title: 'Bug: Duplicate user skill config entries and deletion wipes all rows'
status: Done
assignee:
  - claude
created_date: '2026-05-14 15:04'
updated_date: '2026-05-14 16:15'
labels:
  - bug
  - configuration
  - data-loss
  - ux
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After updating to 0.3.0, the plugin configuration page shows two identical user skill configuration rows for the same user with the same values. 

Steps to reproduce:
1. Install 0.3.0 (upgrade from 0.2.x)
2. Open plugin configuration page
3. Observe two identical user skill config rows

Observed behavior:
- Two duplicate rows appear in the per-user settings section
- Deleting the bottom row succeeds (row disappears) but a toast error appears saying "problem deleting a user"
- After page refresh, **0 rows remain** — all user config entries are gone

Likely root causes to investigate:
1. **Duplicate on load**: The config page JS or the API controller may be returning duplicate entries (e.g., loading from both old and new config format, or a migration creating duplicates)
2. **Delete uses wrong key**: The delete endpoint may be matching by index instead of user ID, causing it to delete the wrong entry or both
3. **Race condition on save**: Deleting one entry and saving may serialize an empty or single-element list, wiping the other entry

Files to check:
- `Configuration/config.html` — client-side user config table rendering and delete logic
- `Configuration/PluginConfiguration.cs` — `PerUserSettings` collection and serialization
- `Controller/PluginController.cs` — API endpoints for listing/deleting per-user config
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Root Cause Analysis

4 bugs found, all in `config.html`:

### Bug 1 & 2: `updatePluginConfiguration` at line 753 sends stale config.Users
- Save handler fires individual POST/PATCH/DELETE API calls (lines 670-751), then immediately sends the FULL stale config object via `updatePluginConfiguration` (line 753)
- The stale config.Users overwrites server-side changes → data loss on delete, duplicates on load
- **Fix**: Remove `Users` from the config object before calling `updatePluginConfiguration`, or avoid the call entirely

### Bug 3: Delete uses `event.target` instead of `e.target` (line 558)
- `event.target` may be a text node, not the button → `data-id` is null → API returns 400
- **Fix**: Change to `e.target.closest("button[data-id]")`

### Bug 4: Missing authorize button after adding new user
- New user JS object has no `UserSkill` property → `canAuthorize` is false → button hidden
- POST (which creates UserSkill) only fires on Save click
- **Fix**: Show a "Save first, then authorize" message for unsaved users

## Implementation Steps
1. Fix Bug 3: `event.target` → `e.target.closest("button[data-id]")`
2. Fix Bugs 1 & 2: Strip `Users` array from config before `updatePluginConfiguration` at line 753
3. Fix Bug 4: Show authorize button or helper text for new unsaved users
4. Write tests for delete/save operations
5. Verify build passes
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Additional UX issue: Missing authorize action after creating new user config

When creating a new per-user config entry (0 rows → add first user), the row appears but the "Authorize" action/button is missing. User must save and refresh the page to see the authorize action. This is confusing — users don't know what to do next after adding a user row.

**Expected**: After adding a new user config row, the authorize action should be immediately visible (or a clear call-to-action shown).

**Actual**: Row appears with fields filled but no visible next step. User has to save → refresh to see the authorize button.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 4 bugs in config.html: (1) stale config.Users overwrite on save, (2) wrong event target on delete button, (3) index-based row removal, (4) missing authorize button for unsaved users. Added 28 controller tests for DELETE/POST/PATCH endpoints. Code review applied: spread destructuring, row.remove(), removed duplicate data-layer tests, reused TestHelpers.CreateTestUser.
<!-- SECTION:FINAL_SUMMARY:END -->

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
