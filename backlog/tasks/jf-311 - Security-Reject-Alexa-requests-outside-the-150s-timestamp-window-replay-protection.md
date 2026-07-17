---
id: JF-311
title: >-
  Security: Reject Alexa requests outside the 150s timestamp window (replay
  protection)
status: Done
assignee: []
created_date: '2026-07-12 14:57'
updated_date: '2026-07-15 07:35'
labels:
  - security
  - replay
milestone: m-6
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:442'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`VerifyAlexaSignature` (`AlexaSkillController.cs:442-478`) uses Alexa.NET `RequestVerification.Verify`, which validates the cert chain, SAN, and body signature — but the code never checks `Request.Timestamp` against a tolerance window. Amazon requires rejecting requests older than 150 seconds. Without it, a captured validly-signed request can be replayed until the signing cert expires. Impact is bounded (replays a linked user's own voice command) so this is medium-low, but it is a required part of Alexa's security model and cheap to add. Verified against code 2026-07-12.

Fix: after deserializing, reject requests whose `Request.Timestamp` is outside a ~150s window from now (allow small clock skew). Add a unit test with a stale timestamp.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Requests with a Request.Timestamp older than ~150s (with small skew allowance) are rejected before handler dispatch
- [ ] #2 Requests within the window continue to process normally
- [ ] #3 Unit test covers both a fresh timestamp (accepted) and a stale timestamp (rejected)
- [ ] #4 Rejection does not leak internal detail to the caller
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
150s replay guard shipped in eea9cea; this pass made it testable (AC #3) by extracting it into a pure AlexaRequestTimestampPolicy.IsWithinWindow(timestamp, now) with the controller capturing UtcNow once (fixes a latent double-read where the check and the log diverged). Added 5 unit tests (fresh, stale-past, stale-future, boundary, 1s-past). Closed in commit c4a5bd7.
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
