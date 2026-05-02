---
id: JF-6
title: Add input validation to all controller endpoints
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:21'
labels: []
milestone: m-1
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Multiple controller endpoints lack input validation:

**AlexaSkillController.cs:**
- Line 102: `state` parameter in account linking not validated (length, format, presence)
- Line 288: Fallback "intent not implemented" response instead of proper error

**LWAController.cs:**
- Line 49: Token format/length not validated before processing

**ConfigurationController.cs:**
- Line 164: JSON deserialization not validated for success
- Line 136: Generic error for ArgumentException, no specific validation messages

**Fix approach:**
1. Add null/empty checks for all required parameters
2. Validate string lengths and formats (URLs, tokens, state parameters)
3. Return specific HTTP status codes (400 Bad Request) with descriptive error messages
4. Add ModelState validation attributes where appropriate
5. Validate redirect_uri format in account linking against allowed patterns
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All controller parameters validated for null/empty
- [ ] #2 URL parameters validated for format
- [ ] #3 Token/state parameters validated for length and format
- [ ] #4 Proper HTTP 400 responses with descriptive messages
- [ ] #5 Redirect URIs validated against allowed patterns
- [ ] #6 Unit tests for validation failure cases
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added input validation to all 3 controllers. AlexaSkillController: required param checks for account-linking, username/password validation, safe Guid parsing for access tokens. ConfigurationController: Guid.TryParse for all userId routes. LWAController: token param validation. All endpoints now return proper HTTP 400 with descriptive messages instead of throwing unhandled exceptions.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
