---
id: JF-283
title: Verify Dynamic Entities SMAPI deployment and resolution
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-10-07 08:43'
labels:
  - e2e
  - smapi
  - dynamic-entities
milestone: m-18
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DynamicEntityBuilder and DynamicEntitiesInterceptor update slot values via SMAPI. Unit tests exist but real SMAPI deployment + Alexa entity resolution is untested. Need to:
1. Trigger dynamic entity update (play an artist → verify artist name added to slot)
2. Deploy updated entities via SMAPI
3. Verify Alexa resolves the new entity value in a subsequent request
4. Test with large libraries (>1000 artists) — verify performance
5. Verify entity cleanup when items are removed from library

AUDIT UPDATE (2026-10-02): the mechanism framing is wrong - dynamic entities inject via the Dialog.UpdateDynamicEntities RESPONSE directive (DynamicEntitiesInterceptor, wired at EntryPoints/Registrator.cs:123), not "via SMAPI" (that is the separate catalog-sync subsystem). The unit layer has grown (12 interceptor tests + builder + ja-budget tests) and live directive delivery is proven by the 2026-08-21 INVALID_RESPONSE incident (CLAUDE.md). GENUINELY REMAINING: positive live proof that a dynamically injected value resolves in a subsequent request, large-library performance, and removal cleanup.
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
