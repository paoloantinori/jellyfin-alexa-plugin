---
id: DRAFT-1
title: Fix FallbackIntentHandler behavior — pre-existing dispatch issue
status: Draft
assignee: []
created_date: '2026-05-08 20:51'
labels:
  - refactor
  - handler
  - bug
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The FallbackIntentHandler has a pre-existing behavior problem that causes unit tests to fail. This predates the current session's work and is related to how fallback intent routing/dispatch works.

**Context:**
- Unit tests for FallbackIntentHandler were already failing before recent changes
- The behavior change is pre-existing, not introduced by recent work
- Related to intent dispatch logic that routes unrecognized utterances

**Scope:**
- Investigate why FallbackIntentHandler tests are failing
- Determine the correct fallback behavior (pass-through to Alexa default vs. custom handling)
- Fix handler logic and update/fix unit tests
- Verify no regression in NLU integration tests
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
