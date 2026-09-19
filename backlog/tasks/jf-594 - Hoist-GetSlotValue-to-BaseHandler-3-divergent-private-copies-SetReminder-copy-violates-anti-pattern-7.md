---
id: JF-594
title: >-
  Hoist GetSlotValue to BaseHandler (3 divergent private copies; SetReminder
  copy violates anti-pattern #7)
status: To Do
assignee: []
created_date: '2026-09-19 01:33'
labels:
  - tech-debt
  - cleanup
milestone: Polish
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
/simplify JF-325 follow-up (SKIP with promise): the slot-value extraction now exists in three private copies with DIVERGENT semantics - PlaylistEditHandlerBase (strict: IsNullOrWhiteSpace + Trim), FindSongIntentHandler (no trim), SetReminderIntentHandler (IsNullOrEmpty, a latent anti-pattern #7 whitespace-only violation). Hoisting the strict variant to BaseHandler and deleting the privates touches two handlers outside JF-325's blast radius, so it was skipped in that change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 BaseHandler (or a shared util) exposes one strict GetSlotValue(intentRequest, slotName) (IsNullOrWhiteSpace + Trim)
- [ ] #2 The three private copies are deleted and their call sites use the shared helper
- [ ] #3 The behavior deltas are stated in the task notes: SetReminder starts rejecting whitespace-only slots (its private copy uses IsNullOrEmpty, a latent anti-pattern #7 violation) and FindSong starts trimming
- [ ] #4 Suite passes both TFMs without --no-build
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
