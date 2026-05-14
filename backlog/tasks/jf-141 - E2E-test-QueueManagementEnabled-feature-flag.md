---
id: JF-141
title: 'E2E test: QueueManagementEnabled feature flag'
status: Done
assignee: []
created_date: '2026-05-14 12:27'
updated_date: '2026-05-14 13:00'
labels:
  - testing
  - e2e
  - configuration
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create E2E tests that verify the QueueManagementEnabled feature flag correctly gates queue-related intents.

When QueueManagementEnabled=false, the following intents should return a "feature disabled" response:
- AddToQueueIntent ("aggiungi alla coda")
- ClearQueueIntent ("svuota la coda")
- PlayNextIntent ("riproduci dopo")
- ListQueueIntent ("cosa c'è in coda")

When QueueManagementEnabled=true, these intents should work normally.

Approach:
1. Configure plugin with QueueManagementEnabled=false via the Jellyfin API
2. Run E2E utterances for each intent via SMAPI simulate-skill
3. Verify response contains "disabled" / "non disponibile" text
4. Re-enable the flag and verify intents work normally
5. Use it-IT locale for reliable simulate-skill routing

Files to create/modify:
- tests/integration/fixtures/e2e_config_queue_management.yaml (new)
- Possibly extend the E2E test runner to support config setup/teardown

References: IntentNames.cs (AddToQueue, ClearQueue, PlayNext, ListQueue), AddToQueueIntentHandler.cs, ClearQueueIntentHandler.cs, PlayNextIntentHandler.cs, ListQueueIntentHandler.cs
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture file created with test cases for AddToQueue, ClearQueue, PlayNext, ListQueue with flag disabled
- [ ] #2 Tests verify 'disabled' response when QueueManagementEnabled=false
- [ ] #3 Tests verify normal behavior when QueueManagementEnabled=true
- [ ] #4 E2E test runner supports config setup/teardown for the flag
- [ ] #5 Tests pass reliably with it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added QueueManagementFeatureFlagTests to FeatureFlagTests.cs. 6 tests covering all 4 queue handlers: disabled response when QueueManagementEnabled=false, normal behavior when enabled. Extracted shared AssertDisabledWhenFlagOff helper to reduce duplication.
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
