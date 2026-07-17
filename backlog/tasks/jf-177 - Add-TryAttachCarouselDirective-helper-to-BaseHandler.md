---
id: JF-177
title: Add TryAttachCarouselDirective helper to BaseHandler
status: Done
assignee: []
created_date: '2026-05-18 13:09'
updated_date: '2026-05-18 14:44'
labels:
  - apl
  - carousel
  - infrastructure
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create a `TryAttachCarouselDirective` method in BaseHandler parallel to the existing `TryAttachListDirective`. This enables any handler to attach an image carousel with one method call when APL is available.

**Implementation:**
- Add method to BaseHandler with signature matching TryAttachListDirective pattern
- Check `AplHelper.DeviceSupportsApl(context) && AplHelper.VisualsEnabled`
- Call `AplHelper.BuildCarouselDirective(title, items, token)` and add to response directives
- No-op on non-APL devices

**Depends on:** JF-176.1 (carousel template, already done)
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

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 TryAttachCarouselDirective method exists on BaseHandler with same signature pattern as TryAttachListDirective
- [ ] #2 Method checks AplHelper.DeviceSupportsApl and AplHelper.VisualsEnabled before attaching
- [ ] #3 No-op on non-APL devices
- [ ] #4 Unit tests verifying attach and no-op behaviors
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added `TryAttachCarouselDirective` to BaseHandler (parallel to TryAttachListDirective) for attaching image carousels with one method call. Checks APL support and visuals enabled, no-op otherwise. Fixed static AplVisualsEnabled test state pollution across 6 test files — added EnsureVisualsEnabled() guards so parallel test classes don't leak disabled state. 3 new unit tests. All 1650 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
