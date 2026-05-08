---
id: JF-47
title: Retry logic with exponential backoff for API calls
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 16:04'
labels:
  - enhancement
  - resilience
  - error-handling
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add retry logic with exponential backoff for failed Jellyfin API calls. Currently, a single failed API call results in an immediate error response to the user.

Implementation: Wrap Jellyfin API calls in a retry policy (e.g., Polly library, which is already common in .NET/Jellyfin ecosystem). Retry transient failures (HTTP 5xx, timeouts) up to 3 times with exponential backoff (e.g., 500ms, 1s, 2s). Only surface error to user after all retries exhausted. Log each retry attempt for diagnostics.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added RetryHelper utility with sync (Func&lt;T&gt;) and async (Func&lt;Task&lt;T&gt;&gt;) overloads using exponential backoff (500ms, 1s, 2s). Wrapped all LibraryManager/SessionManager calls in 16 intent handlers with retry via BaseHandler.RetryAsync helper. Wrapped LWA HTTP calls in LwaClient with retry. Proper cancellation handling: user-initiated cancellation propagates immediately, only genuine transient failures (HttpRequestException, TimeoutException, HTTP timeout without user cancellation) are retried. LwaClient logger uses Plugin.Instance.LoggerFactory for proper DI integration. 15 unit tests for RetryHelper. All 420 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
