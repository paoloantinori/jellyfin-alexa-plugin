---
id: JF-230
title: Add Utterance Profiler script for fast NLU pre-checks
status: Done
assignee: []
created_date: '2026-05-29 20:47'
updated_date: '2026-05-29 20:52'
labels:
  - testing
  - nlu
  - smapi
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html
  - claudedocs/research_alexa_console_features_2026-05-29.md
documentation:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create a `scripts/profile_utterances.sh` script that uses the Alexa Utterance Profiler SMAPI API (`ask smapi profile-utterance`) to batch-test utterance routing against the interaction model without requiring a full model build or skill backend.

**Why**: Our current NLU test pipeline (`run_nlu_tests.sh`) uses `simulate-skill` which requires a built model, a live skill endpoint, and ~1.5s delay between calls. The Utterance Profiler tests intent/slot resolution directly against the saved model — no build, no endpoint, much faster. This gives us a lightweight pre-check to catch NLU regressions before committing to a full NLU test run.

**Approach**: Reuse the existing YAML fixture format from `tests/integration/fixtures/<locale>.yaml` (which already has `utterance`, `expected_intent`, `expected_slots` fields). Parse fixtures, call the profiler API for each utterance, compare resolved intent/slots against expected, report pass/fail.

**SMAPI API details**:
- Endpoint: `POST /v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}/profileNlu`
- Request body: `{"utterance": "play music by pink floyd"}`
- Response: resolved intent name, slot values, and confidence
- CLI: `ask smapi profile-utterance --skill-id <ID> --stage development --locale <LOC> --utterance "<text>"`
- Docs: https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html

**Integration points**:
- New script: `scripts/profile_utterances.sh` (bash wrapper, like `run_nlu_tests.sh`)
- New Python module: `tests/integration/test_utterance_profiler.py` (or similar) that parses fixtures and calls the profiler
- CI: Add as advisory step in CI (like `validate_interaction_models.py` — non-blocking initially)
- Can run alongside or before `run_nlu_tests.sh` as a fast gate
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Script `scripts/profile_utterances.sh` accepts locale filter (-k), skill ID (--skill-id or $ASK_SKILL_ID), and supports --dry-run mode
- [x] #2 Script reuses existing YAML fixtures from tests/integration/fixtures/<locale>.yaml — same format, same test cases
- [x] #3 For each fixture utterance, calls the Utterance Profiler SMAPI API and compares resolved intent + slots against expected values
- [x] #4 Outputs a summary: total tested, passed, failed (with details on mismatches: expected vs actual intent/slots)
- [x] #5 Exit code 0 on all pass, 1 on any failure (compatible with CI pipelines)
- [x] #6 Respects SMAPI_DELAY between API calls to avoid rate limiting
- [x] #7 Supports --dry-run flag that validates fixtures exist and are parseable without making SMAPI calls
- [x] #8 Documented usage in CLAUDE.md alongside the existing NLU/E2E test commands
<!-- AC:END -->



## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Task was already implemented. The existing `run_nlu_tests.sh` + `test_nlu.py` + `smapi_client.py` already use the Utterance Profiler API (`ask smapi profile-nlu`). All 8 acceptance criteria were already met. Only gap was CLAUDE.md documentation — updated to clarify that NLU tests use the Profiler API (no model build or endpoint needed) vs E2E tests that use simulate-skill (full pipeline). Added `--dry-run` example to Build & Test section.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
