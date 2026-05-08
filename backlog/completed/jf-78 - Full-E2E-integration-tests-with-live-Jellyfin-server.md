---
id: JF-78
title: Full E2E integration tests with live Jellyfin server
status: Done
assignee: []
created_date: '2026-05-04 19:16'
updated_date: '2026-05-05 23:13'
labels:
  - testing
  - alexa
  - ask-cli
  - e2e
  - jellyfin
dependencies:
  - JF-77
references:
  - scripts/validate_model.sh
documentation:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html
  - 'https://jellyfin.org/docs/general/networking/api'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extend the ASK CLI integration test suite (JF-77) to exercise the full chain: Alexa utterance → NLU → skill endpoint → Jellyfin API → response validation. This requires a running Jellyfin server accessible to the skill endpoint.

**Architecture**: The skill is a Jellyfin plugin (not a Lambda). When `ask smapi simulate-skill` sends a request, Alexa's service calls the skill's endpoint (the Jellyfin server URL configured in the Alexa developer console). So the Jellyfin server must be internet-reachable OR tunneled via ngrok.

**Test flow**:
1. Send utterance via SMAPI simulate-skill
2. Validate NLU resolution (intent + slots) — same as JF-77
3. Additionally validate the skill's response: output speech content, card title, reprompt
4. Optionally query Jellyfin API to verify side effects (media started playing, queue updated)

**Dependency**: Requires JF-77 (NLU test suite) to be completed first — this task builds on its pytest infrastructure, SMAPI client, and fixture format.

**Key difference from JF-77**: This task validates the full round-trip including the skill's response behavior, not just NLU resolution. It also verifies Jellyfin server side-effects via the Jellyfin REST API.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Extends the pytest infrastructure from JF-77 (shared fixtures, conftest, SMAPI client)
- [ ] #2 Test fixture YAML files include `expected_response_contains` or `expected_output_speech` assertions
- [ ] #3 Test runner accepts --jellyfin-url, --jellyfin-api-key, --jellyfin-user as CLI args or env vars
- [ ] #4 Supports both local Jellyfin (localhost) and remote (ngrok/tunnel) configurations
- [ ] #5 Health-check fixture verifies Jellyfin server is reachable before running tests (skip gracefully if not)
- [ ] #6 Tests validate: media actually starts playing (via Jellyfin /Sessions API), correct item resolved, queue state correct
- [ ] #7 Cleanup fixture stops playback and clears queue after each test
- [ ] #8 Separate pytest marker (e.g., @pytest.mark.e2e) so E2E tests can be run independently from NLU-only
- [ ] #9 Runner script `scripts/run_e2e_tests.sh` with optional Jellyfin connection parameters
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created full E2E integration test infrastructure for the Jellyfin Alexa skill:
- `jellyfin_client.py`: REST API client for side-effect verification (playback state, favorites, search)
- `test_e2e.py`: Full-chain test using SMAPI simulate-skill with intent, slot, response type, and Jellyfin side-effect assertions
- `e2e_en-US.yaml`: 8 test cases covering playback, info, state change, and queue management intents
- Extended `conftest.py` with E2E CLI options, markers, Jellyfin client fixture, and cleanup
- `run_e2e_tests.sh`: Runner script with venv setup
- Tests auto-skip without Jellyfin connection params; support --dry-run for fixture validation

Simplify fixes applied: merged duplicate response traversal into single parser, eliminated double API call in side-effect check, extracted shared parametrize helper, removed unused parameters.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
