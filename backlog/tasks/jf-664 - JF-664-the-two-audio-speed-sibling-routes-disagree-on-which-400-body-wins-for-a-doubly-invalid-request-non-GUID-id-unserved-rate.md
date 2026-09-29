---
id: JF-664
title: >-
  JF-664 - the two audio-speed sibling routes disagree on which 400 body wins
  for a doubly-invalid request (non-GUID id + unserved rate)
status: Done
assignee: []
created_date: '2026-09-29 01:55'
updated_date: '2026-09-29 13:43'
labels:
  - refactor
  - streaming
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-651 -
    JF-651-the-Guid.TryParseValidateStreamToken-route-preamble-repeats-at-~9-VideoAudioController-entries-extract-one-shared-signed-route-validator-preserving-each-routes-pinned-400-401-ordering.md
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-651 simplify/altitude review round (the altitude agent surfaced it while verifying the kept-separate comments; filing immediately per the review-recommendation discipline).

THE FINDING: for a request that is doubly invalid (a non-GUID itemId AND an unserved playback rate), the two audio-speed sibling endpoints answer with DIFFERENT 400 bodies, and the disagreement is accidental, not decided:

- `StreamHlsAudioSpeed` (the playlist route, `Controller/VideoAudioController.cs`): the token-gated preamble skips the id 400 for a non-GUID, and `StreamHlsAudioSpeedCore` checks `IsValidPerMille` BEFORE `ValidateVideoAudioRequest`'s id 400, so the response is 400 "Unsupported playback rate".
- `GetAudioSpeedSegment` (the segment route): the id 400 runs first, so the same doubly-invalid shape answers 400 "Invalid itemId format".

Both are 400s and no client is known to depend on either body, but the contract should be decided once rather than inherited from code order. JF-651 deliberately preserved both orderings byte-for-byte (the kept-separate comments at both routes name them), so this task is decision work, not a regression fix.

THE WORK: decide the precedence for doubly-invalid requests (id 400 first is the natural reading, matching every other route), apply it to whichever route diverges, and pin it with a test. Coordinate with the JF-637 follow-up family if that round restructures these routes' validation pipelines anyway.

VERIFICATION: the new pin test plus the existing StreamHlsAudioSpeed/GetAudioSpeedSegment suites stay green; no other route's ordering changes.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as merge c6e52691 on main: the doubly-invalid audio-speed shape (non-GUID id + unserved rate) now answers 400 Invalid itemId format on BOTH sibling routes. StreamHlsAudioSpeed migrated its JF-651-era hand-rolled preamble (a Guid.TryParse-gated token check that skipped validation for non-GUIDs and let the Core's rate check answer first) to the shared ValidateSignedRoute; GetAudioSpeedSegment needed no change. Every other pinned ordering stayed byte-identical (GUID + bad rate + no token keeps its 401, non-GUID + served rate keeps the same id-400 body, GUID + unserved rate + valid token keeps the Core's rate 400). Pin: AudioSpeedRoutes_DoublyInvalid_NonGuidIdPlusUnservedRate_BothReturnItemId400, the first body-asserting pin in the controller test file (Assert.IsType alone could not discriminate; the old playlist body was a disjoint 400). The residual triply-invalid divergence (GUID + unserved rate + bad token: playlist 401 vs segment rate-400, both unpinned) is named in the route comment and filed same-turn as JF-671. Gates: Skill simplify (4-agent round, all angles clean) and Skill code-review high in-worker (3 findings: comment scoped, routeError rename, one skip-justified) plus the orchestrator gate-marker code-review pass (clean; its one low finding, the JF-671 reference pointing at the pre-rename jf-664 filename, fixed in the same DONE commit as the rename). Suites: 4736/4736 both TFMs worker-run and orchestrator-verified independently; merged-tree (JF-664+JF-667) 4741/4741 both TFMs. N/A DoD: NLU fixtures, E2E-for-new-intent, locale strings (internal error contract, no model or handler surface). Live probe after deploy: curl both audio-speed routes with not-a-guid + rate expects the id-format 400 body on both.
<!-- SECTION:FINAL_SUMMARY:END -->
