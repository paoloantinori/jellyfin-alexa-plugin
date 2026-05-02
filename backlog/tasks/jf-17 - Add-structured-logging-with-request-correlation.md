---
id: JF-17
title: Add structured logging with request correlation
status: Done
assignee:
  - claude
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 08:03'
labels:
  - debugging
  - logging
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MEDIUM: Logging lacks request correlation and uses string.Format-style {0} instead of named template parameters.

Changes:
- Fix all _logger calls to use named templates: {0} -> {RequestType}, {UserId}, etc.
- Add ILogger.BeginScope() in HandleIntentRequest with RequestId, UserId, DeviceId, RequestType
- Log full error context in ExceptionHandler (ErrorType, Message, Token, DeviceId)
- Log PlaybackFailed error details (ErrorType, Message, Token)
- Log SessionEndedRequest.Reason and Error details
- Add startup diagnostic log in Plugin.cs constructor
- Add request/response JSON logging at DEBUG level
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented structured logging with request correlation across all handlers and controllers.

**Changes:**
- Replaced all positional `{0}` parameters with named templates (`{UserId}`, `{RequestType}`, `{RedirectUri}`, etc.) in AlexaSkillController, LWAController, BaseHandler
- Added `BeginScope` correlation in HandleIntentRequest with RequestId, UserId, DeviceId, RequestType
- Enhanced ExceptionHandler: logs ErrorType, ErrorMessage, RequestId, DeviceId
- Enhanced PlaybackFailedEventHandler: logs ItemId, OffsetMs, RequestId, DeviceId
- Enhanced SessionEndedRequestHandler: logs Reason, Error details (Type/Message), RequestId
- Added Plugin.cs startup diagnostic log with version
- Added DEBUG-level request body and response logging

**Tests:** 10 new structured logging tests (all passing), total suite: 140 passed, 0 failed.
<!-- SECTION:FINAL_SUMMARY:END -->
