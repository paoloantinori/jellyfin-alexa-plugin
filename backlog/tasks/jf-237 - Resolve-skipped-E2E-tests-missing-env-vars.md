---
id: JF-237
title: Resolve skipped E2E tests (missing env vars)
status: Done
assignee: []
created_date: '2026-05-30 08:43'
updated_date: '2026-05-30 10:20'
labels: []
dependencies:
  - JF-231
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
3 E2E tests (soul coughing scenarios) were skipped because $JELLYFIN_URL, $JELLYFIN_API_KEY, and $JELLYFIN_USER env vars were not set in the test shell.

Options:
1. Document that these env vars must be exported before running E2E tests (they exist in .claude.local.md but aren't auto-exported)
2. Update run_e2e_tests.sh to read from .claude.local.md or a .env file
3. Accept that E2E tests are manual-only and require explicit env var setup

Fixture: tests/integration/fixtures/e2e_it-IT.yaml
Script: scripts/run_e2e_tests.sh
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
E2E test skips are by design — they require a live Jellyfin endpoint ($JELLYFIN_URL, $JELLYFIN_API_KEY, $JELLYFIN_USER) which isn't configured in CI or the default shell. The test framework correctly skips when env vars are missing. No code change needed; this is working as intended.
<!-- SECTION:FINAL_SUMMARY:END -->
