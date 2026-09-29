---
id: JF-671
title: >-
  JF-671 - pin or decide the triply-invalid audio-speed shapes (GUID + unserved
  rate + bad token): siblings disagree, unpinned
status: To Do
assignee: []
created_date: '2026-09-29 12:49'
labels:
  - streaming
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-664 -
    JF-664-the-two-audio-speed-sibling-routes-disagree-on-which-400-body-wins-for-a-doubly-invalid-request-non-GUID-id-plus-unserved-rate.md
  - >-
    backlog/tasks/jf-651 -
    JF-651-the-Guid.TryParseValidateStreamToken-route-preamble-repeats-at-~9-VideoAudioController-entries-extract-one-shared-signed-route-validator-preserving-each-routes-pinned-400-401-ordering.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-664 code-review gate (high-effort round, finding 1).

THE RESIDUAL DIVERGENCE: JF-664 aligned the two audio-speed sibling routes on the DOUBLY-invalid shape (non-GUID id + unserved rate -> both answer 400 "Invalid itemId format"). The TRIPLY-invalid shape is still deliberately split and UNPINNED:
- StreamHlsAudioSpeed (playlist route, Controller/VideoAudioController.cs): id -> token -> rate; a GUID id + unserved rate + missing/invalid token answers 401 "Invalid or expired stream token".
- GetAudioSpeedSegment (segment route): id -> rate -> token; the same shape answers 400 "Unsupported playback rate". Its JF-651 kept-separate comment documents the order as deliberate.

Neither ordering is locked by any test today: StreamHlsAudioSpeed_NoToken_Returns401 uses a served rate (1500), and the segment route's rate leg in GetAudioSpeedSegment_ServesVariantDirectory_AndRejectsMissingToken runs with a valid token. So a future "align the segment route to the shared preamble" migration would silently flip unpinned behavior - the same accidental-disagreement shape JF-664 just closed for the doubly-invalid case.

THE WORK: decide which route is right for the triply-invalid shape (or ratify the split as intentional), pin BOTH routes' current 400/401 answers for GUID id + unserved rate + bad token with tests in the style of the existing audio-speed pins (Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs), and update the JF-664 comment on StreamHlsAudioSpeed (it names this follow-up) if the decision changes anything.

CONSTRAINTS: whatever is decided, single-invalid shapes (GUID + served rate + no token -> 401; GUID + unserved rate + valid token -> 400 rate on both routes) are pinned by existing tests and must not move.

VERIFICATION: the new pins plus the existing StreamHlsAudioSpeed/GetAudioSpeedSegment suites stay green; no other route's ordering changes.
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
