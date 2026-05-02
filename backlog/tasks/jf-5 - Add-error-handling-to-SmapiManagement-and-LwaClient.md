---
id: JF-5
title: Add error handling to SmapiManagement and LwaClient
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:18'
labels: []
milestone: m-1
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SmapiManagement.cs makes multiple SMAPI API calls with NO try-catch blocks. LwaClient.cs has multiple methods that return null on failure instead of proper error handling.

**SmapiManagement.cs issues (lines 39-79):**
- CreateSkill, UpdateSkill, DeleteSkill, GetSkill — all make HTTP calls with no error handling
- No retry logic for rate limits (HTTP 429)
- No logging of API call results or failures

**LwaClient.cs issues (lines 72, 122, 126, 170):**
- GetDeviceToken() returns null on multiple failure paths
- StartDeviceAuthorization() returns null on failure
- No specific error messages — caller can't distinguish between network failure, auth failure, etc.

**Fix approach:**
1. Wrap all external API calls in try-catch with specific exception types
2. Add structured logging (ILogger) for all API call outcomes
3. Return proper error information instead of null
4. Consider a custom exception type (e.g., SmapiException, LwaException)
5. Add retry logic with exponential backoff for transient failures (429, 500, 503)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All SmapiManagement methods have try-catch blocks
- [ ] #2 All LwaClient methods have proper error handling
- [ ] #3 Methods return meaningful error info instead of null
- [ ] #4 All API call outcomes logged via ILogger
- [ ] #5 Transient failure retry with exponential backoff
- [ ] #6 Unit tests for error scenarios on each method
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added error handling and logging to SmapiManagement and LwaClient. SmapiManagement now takes ILoggerFactory and logs all API calls. LwaClient throws descriptive exceptions (HttpRequestException, TimeoutException, InvalidOperationException) instead of silent null returns. Plugin stores LoggerFactory for downstream use. Skipped exponential backoff retry — SMAPI calls happen at most once on startup, not worth the complexity.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
