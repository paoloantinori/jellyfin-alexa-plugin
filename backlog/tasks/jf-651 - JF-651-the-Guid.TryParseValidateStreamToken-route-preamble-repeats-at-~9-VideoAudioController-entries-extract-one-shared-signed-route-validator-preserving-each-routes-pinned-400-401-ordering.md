---
id: JF-651
title: >-
  JF-651 - the Guid.TryParse+ValidateStreamToken route preamble repeats at ~9
  VideoAudioController entries: extract one shared signed-route validator,
  preserving each route's pinned 400/401 ordering
status: To Do
assignee: []
created_date: '2026-09-27 08:23'
labels:
  - refactor
  - streaming
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 altitude review's item-4 verification (the Guid.TryParse guard investigation walked all the route entries on the way to its verdict).

THE FINDING: the identical request-preamble pair (Guid.TryParse on the route's itemId + ValidateStreamToken for the token check) repeats at ~9 route entries in Controller/VideoAudioController.cs (line anchors at review time: ~221, ~425, ~711, ~1239, ~1332, ~1411, ~1933, ~1982, ~2444). Each copy re-implements the same shape: parse the id, return BadRequest on parse failure, then validate the signed stream token, returning 401 on failure. The JF-637 round deliberately did NOT consolidate these (out of its diff's scope).

THE WORK: extract one shared preamble helper (e.g. ValidateSignedRoute(itemId, token) returning the ValidatedRequest-or-error the routes already consume), migrate all ~9 entries to it. BEHAVIOR CONTRACT to preserve exactly: the 400-vs-401 ordering per route (the altitude review verified the ordering is the documented contract at the speed route: a non-GUID reaches the BadRequest path and yields 400, pinned by StreamHlsAudioSpeed_InvalidItemId_Returns400; missing token on a valid GUID yields 401, pinned by the NoToken sibling test). Read every entry before migrating: any entry whose ordering or error shape differs stays separate with a comment.

VERIFICATION: the existing VideoAudioControllerTests preamble tests stay green unchanged; grep confirms zero remaining inline copies; build 0 errors, suite green both TFMs.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
