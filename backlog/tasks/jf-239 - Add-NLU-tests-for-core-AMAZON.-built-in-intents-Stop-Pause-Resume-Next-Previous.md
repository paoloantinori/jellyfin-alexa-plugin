---
id: JF-239
title: >-
  Add NLU tests for core AMAZON.* built-in intents (Stop, Pause, Resume, Next,
  Previous)
status: Done
assignee: []
created_date: '2026-06-01 09:07'
updated_date: '2026-06-01 09:43'
labels:
  - testing
  - nlu
dependencies: []
references:
  - tests/integration/fixtures/it-IT.yaml
  - tests/integration/fixtures/en-US.yaml
  - tests/integration/test_nlu.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The 5 most-used AMAZON.* built-in intents (Stop, Pause, Resume, Next, Previous) have zero NLU test coverage in any locale. These are Alexa-provided intents with no custom samples, but NLU tests verify the interaction model correctly registers them and the invocation name resolves.

**Why**: These intents were involved in the recent stop/pause routing fix (`ShouldEndSession=true`). If a model regeneration or SMAPI deployment drops them, there's no automated signal. A single NLU test per intent acts as a regression guard.

**Implementation plan**:
1. Add 5 entries to `tests/integration/fixtures/it-IT.yaml`:
   - `"ferma"` → `AMAZON.StopIntent`
   - `"pausa"` → `AMAZON.PauseIntent`
   - `"riprendi"` → `AMAZON.ResumeIntent`
   - `"avanti"` → `AMAZON.NextIntent`
   - `"indietro"` → `AMAZON.PreviousIntent`
2. Add 5 matching entries to `tests/integration/fixtures/en-US.yaml`:
   - `"stop"` → `AMAZON.StopIntent`
   - `"pause"` → `AMAZON.PauseIntent`
   - `"resume"` → `AMAZON.ResumeIntent`
   - `"next"` → `AMAZON.NextIntent`
   - `"previous"` → `AMAZON.PreviousIntent`
3. Validate with `./scripts/run_nlu_tests.sh --dry-run`
4. All slots should be `expected_slots: {}` (built-in intents have no custom slots)

**Note**: AMAZON.* intents are Alexa built-ins — they don't have custom utterance samples in the model. The NLU test verifies the intent is registered and the utterance resolves correctly against the invocation context. Some utterances like bare "stop" may not resolve without invocation context — use "chiedi a mia collezione di fermare" or similar if bare forms don't work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 it-IT.yaml has NLU test entries for: ferma→AMAZON.StopIntent, pausa→AMAZON.PauseIntent, riprendi→AMAZON.ResumeIntent, avanti→AMAZON.NextIntent, indietro→AMAZON.PreviousIntent
- [ ] #2 en-US.yaml has NLU test entries for: stop→AMAZON.StopIntent, pause→AMAZON.PauseIntent, resume→AMAZON.ResumeIntent, next→AMAZON.NextIntent, previous→AMAZON.PreviousIntent
- [ ] #3 NLU tests pass (dry-run or live)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added NLU test fixtures for 5 AMAZON.* built-in intents (Stop, Pause, Resume, Next, Previous) in both it-IT and en-US locales. 10 new test entries total. Dry-run validates fixtures correctly (492 items collected).
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
