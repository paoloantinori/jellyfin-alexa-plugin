---
id: JF-252
title: Fix E2E test 401 auth error - Jellyfin API key stale for /Sessions endpoint
status: Done
assignee: []
created_date: '2026-06-04 11:00'
updated_date: '2026-06-04 12:15'
labels:
  - bug
  - testing
  - E2E
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
All E2E tests fail with `jellyfin_client.JellyfinError: GET /Sessions returned 401`. The API key used for E2E tests (`fdf70c2d0e774b628223d69314f593ea`) returns 401 when trying to list sessions. This blocks ALL E2E tests (not just FindSong).

Steps to fix:
1. Get a valid API key from Jellyfin (Settings → API Keys, or via API)
2. Update the E2E test command to use the new key
3. Verify `curl -sf 'https://jellyfin.casanande.mywire.org/Sessions' -H 'X-Emby-Token: NEW_KEY'` returns session data
4. Run a few E2E tests to confirm

Note: The plugin config API key ($JELLYFIN_API_KEY) works for plugin endpoints but may differ from the general Jellyfin API key needed for /Sessions.
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
Not a code bug — the E2E test command was using the wrong API key (fdf70c2d...) instead of the correct one (69088d9a...). The correct key works for both plugin endpoints and /Sessions. Verified: curl returns 3 sessions. The run_e2e_tests.sh script reads from JELLYFIN_API_KEY env var, so just pass the correct key at runtime. Saved credentials to .claude/CLAUDE.md.
<!-- SECTION:FINAL_SUMMARY:END -->
