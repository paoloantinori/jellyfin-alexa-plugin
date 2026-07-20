---
id: JF-359
title: >-
  E2E/unit tests do not assert Alexa 8s response-budget — latency regressions go
  uncaught
status: To Do
assignee: []
created_date: '2026-07-20 16:14'
labels:
  - testing
  - ci
  - regression
  - performance
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The announce-feature deploy on 2026-07-20 surfaced a test-coverage gap: the slow PlayArtistSongs DB query (JF-358) caused Alexa's INVALID_RESPONSE, but NO existing test would have caught it because none measures live response latency against Alexa's 8-second budget.

Current test coverage and why each misses a latency regression:
- Unit tests (~2558): assert response correctness (OutputSpeech, directives) with mocked Jellyfin data — no real DB, no timing.
- E2E (run_e2e_tests.sh / SMAPI simulate-skill): assert the returned JSON structure is valid — a 14-second response still passes because the test inspects the produced response, not when it arrived.
- NLU tests (profile-nlu): test intent/slot routing only.
- feedback_hang_fix memory added timeouts to HTTP calls, but nothing asserts the END-TO-END response time.

GOAL: add a regression test that fails when a play-path response exceeds Alexa's response budget, so a latency regression is caught before deploy, not by a live user.

Options to evaluate (pick the most reliable):
1. An E2E test that times the simulate-skill round-trip and asserts < ~7s (leaving margin under Alexa's 8s). Caveat: simulate-skill has its own SMAPI latency that may not reflect the live Echo path — verify the timing is meaningful before relying on it.
2. A unit/integration test that asserts the handler returns within N ms given a mocked-but-realistic slow ILibraryManager (proves the progressive-response/timeout mechanism, not the raw DB speed).
3. A CI gate that runs the slow-path query against a test Jellyfin and asserts p95 < 8s.

Acceptance criteria:
- At least one test exists that FAILS if a play-path response (PlayArtistSongs as the canonical case) would exceed Alexa's response budget — whether by timing the real path or by asserting the progressive-response-is-sent-early invariant from JF-358.
- The test is wired into CI (ci.yml build-and-test or a dedicated job).
- Documented in CLAUDE.md test section that E2E correctness tests do NOT cover response latency, and which test does.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
