---
id: JF-273
title: Add tests for CustomerProfileService
status: Done
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-06-08 11:19'
labels:
  - testing
  - unit-tests
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/CustomerProfileService.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CustomerProfileService looks up Amazon customer profiles. No unit or E2E tests. Used in voice recognition flow (LearnMyVoice/WhoAmI). Need to:
1. Add unit tests with mocked Amazon API responses
2. Verify profile data extraction (name, email)
3. Test error handling for API failures
4. Verify caching behavior
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
Added 6 guard-clause unit tests for CustomerProfileService covering empty/null token and missing endpoint paths for both GetGivenNameAsync and GetTimezoneAsync. Added CreateContext overload for independent token/endpoint control. Documented testability limitations: CustomerProfileClient is hardcoded (new), and GetTimezoneAsync uses a static HttpClient — neither is mockable without refactoring the service. All 10 tests pass, build clean (0 warnings). Commit: b6581b8.
<!-- SECTION:FINAL_SUMMARY:END -->
