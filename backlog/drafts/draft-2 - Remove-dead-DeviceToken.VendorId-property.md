---
id: DRAFT-2
title: Remove dead DeviceToken.VendorId property
status: Draft
assignee: []
created_date: '2026-05-08 20:51'
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
<!-- DOD:END -->
