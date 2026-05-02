---
id: JF-16
title: Add top-level error handling returning valid SkillResponse JSON
status: Done
assignee: []
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 07:10'
labels:
  - robustness
  - certification
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CRITICAL for certification: Several code paths return HTTP 400/204/500 instead of valid SkillResponse JSON. Alexa requires valid SkillResponse for ALL requests.

Fix:
- Wrap AlexaSkillController.HandleIntentRequest in try/catch returning ResponseBuilder.Tell(error) 
- Fix null-request path to return SkillResponse (not NoContentResult)
- Fix non-Intent-request path to return SkillResponse (not BadRequestResult)
- Add error reference IDs to exception responses for traceability
- Ensure PlaybackFailedEventHandler returns ResponseBuilder.Empty() (not Tell with error speech)

Reference: Amazon certification requires valid JSON responses for all error states.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added top-level error handling returning valid SkillResponse JSON for all code paths:

**Controller changes (AlexaSkillController.cs):**
- Wrapped HandleIntentRequest in try/catch returning SkillResponse for all exceptions
- Added OperationCanceledException handler for timeout (6s CancellationToken)
- Added error reference IDs (first 8 chars of GUID) for exception traceability
- Null request path now returns ResponseBuilder.Empty() instead of NoContentResult (HTTP 204)
- Missing/invalid token paths now return SkillResponse with helpful message instead of BadRequestResult/UnauthorizedResult
- Unhandled request types now return ResponseBuilder.Empty() instead of BadRequestResult (HTTP 400)
- Extracted SkillResponseContent() helper method with proper Content-Type header

**PlaybackFailedEventHandler:**
- Changed from ResponseBuilder.Tell(error speech) to ResponseBuilder.Empty()
- Added error logging for the failed item ID
- Alexa certification: PlaybackFailed responses should be empty, not spoken errors

**Verification:** Build 0 errors, 130/130 tests pass
<!-- SECTION:FINAL_SUMMARY:END -->
