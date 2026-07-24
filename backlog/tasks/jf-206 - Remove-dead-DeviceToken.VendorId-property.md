---
id: JF-206
title: Remove dead DeviceToken.VendorId property
status: Done
assignee: []
created_date: '2026-05-08 20:51'
updated_date: '2026-05-22 20:37'
labels:
  - refactor
  - cleanup
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The `DeviceToken.VendorId` property appears to be unused/dead code. Cleanup requires a larger refactor scope because:

**Context:**
- Property exists on `DeviceToken` entity but is not referenced by any active code paths
- Removing it may involve EF migration considerations if it's mapped to a database column
- Need to verify no SMAPI or LWA flows depend on it before removal

**Scope:**
- Audit all references to `VendorId` on `DeviceToken`
- Confirm it's safe to remove (no DB migration needed, or plan migration)
- Remove property and clean up any related dead code
- Verify tests still pass
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Removed `DeviceToken.VendorId` property — dead code with zero references, superseded by `User.VendorId`. No EF migration needed (POCO, not database-mapped). Build clean, 1830 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
