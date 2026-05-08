---
id: JF-77
title: ASK CLI NLU integration test suite
status: Done
assignee: []
created_date: '2026-05-04 19:15'
updated_date: '2026-05-04 20:33'
labels:
  - testing
  - alexa
  - ask-cli
dependencies: []
references:
  - scripts/validate_model.sh
documentation:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build a Python+pytest integration test suite that uses the ASK CLI (`ask smapi simulate-skill`) to validate utterance → intent resolution across all 12 locales. This catches interaction model regressions that unit tests cannot (wrong intent resolution, missing slot values, broken utterances).

The skill is already deployed in "development" stage with ASK CLI v2.30.7 configured. The project has 32 custom intents and 12 locale models in `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json`.

**Why Python+pytest over bash**: Proper assertion library, parametrized tests per locale/intent, structured JSON parsing, fixture-based test data, pytest-xdist for parallel execution, JUnit XML output for CI.

**Key context**:
- ASK CLI auth is already set up (token in `~/.ask/cli_config`)
- Skill ID auto-detection exists in `scripts/validate_model.sh` (reuse the logic)
- SMAPI simulation rate limit: ~50 req/min — batch with delays
- The `simulate-skill` SMAPI endpoint returns the full NLU result: resolved intent, slots, dialog state
- Test data (utterance → expected intent+slots) should live in YAML/JSON fixtures per locale
- Must work without a live Jellyfin server — NLU validation only checks intent resolution, not skill response content
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Python pytest project in `tests/integration/` with its own requirements.txt
- [ ] #2 YAML fixture files defining utterance → expected intent+slots mappings, at least for en-US and it-IT
- [ ] #3 Test suite runs `ask smapi simulate-skill` via subprocess and parses JSON responses
- [ ] #4 Parametrized tests: one test case per utterance fixture entry, grouped by locale
- [ ] #5 Asserts: resolved intent matches expected, required slots are present and correctly typed
- [ ] #6 CLI runner script `scripts/run_nlu_tests.sh` that sets up venv, installs deps, and runs pytest
- [ ] #7 Handles SMAPI rate limiting (respects ~50 req/min with configurable delay)
- [ ] #8 Clear pass/fail output showing which utterances resolved incorrectly with actual vs expected
- [ ] #9 Works offline for fixture validation (dry-run mode that checks fixture schema without hitting SMAPI)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented Python+pytest NLU integration test suite using ASK CLI `ask smapi simulate-skill`.

**Files created:**
- `tests/integration/conftest.py` — pytest fixtures (auto skill-id from `~/.ask/ask_states.json`), YAML fixture parametrization, `--dry-run` mode, `nlu`/`locale` markers
- `tests/integration/smapi_client.py` — `SmapiClient` class wrapping ASK CLI subprocess with module-level rate limiting
- `tests/integration/test_nlu.py` — parametrized `test_utterance_resolves_correct_intent` asserting intent + slot resolution
- `tests/integration/fixtures/en-US.yaml` — 74 test cases covering all 32 custom intents
- `tests/integration/fixtures/it-IT.yaml` — 58 test cases covering all 27 custom intents
- `tests/integration/requirements.txt` — pytest, pyyaml, pytest-xdist
- `scripts/run_nlu_tests.sh` — venv setup + pytest runner

**Usage:** `./scripts/run_nlu_tests.sh --dry-run` (validate fixtures) or `./scripts/run_nlu_tests.sh` (live SMAPI)
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
