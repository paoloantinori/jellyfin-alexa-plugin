---
id: JF-394
title: >-
  Bug: resume_state survives the user's 'no' - stray later 'yes' resumes the
  declined item
status: Done
assignee: []
created_date: '2026-08-23 05:56'
updated_date: '2026-08-23 06:10'
labels:
  - bug
  - multi-turn
  - session-state
milestone: m-15
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
BUG (found by the 2026-08-23 multi-turn audit). NoIntentHandler.HandleResumeRejection builds its FreshStart Ask response with NO SessionAttributes, so SessionAttributesInterceptor merges the incoming attributes back INCLUDING resume_state. Consequence: after the user declines a resume, a stray "yes" in a later turn still resumes the item they just declined (HandleResumeConfirmation finds the stale resume_state).

Fix: clear resume_state (build the rejection response with an explicit attributes dict that drops the resume_state key, or remove the key in the response before returning). Verify with a unit test: resume offer -> "no" -> "yes" must NOT resume (expect UnexpectedYes or fresh-session behavior).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in fd69f7f (TDD: 2 interceptor marker tests + 1 handler+interceptor regression test, all RED-first). Root cause: rejection Ask carried no attributes so the interceptor merged resume_state back. Fix: SessionAttributeRemoval marker contract (__remove_attributes) honored by SessionAttributesInterceptor; HandleResumeRejection marks resume_state. Generic mechanism available for other flows (JF-398 direction).
<!-- SECTION:FINAL_SUMMARY:END -->
