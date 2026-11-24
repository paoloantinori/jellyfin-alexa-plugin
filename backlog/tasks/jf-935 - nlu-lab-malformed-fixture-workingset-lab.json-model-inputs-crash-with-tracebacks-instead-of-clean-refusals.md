---
id: JF-935
title: >-
  nlu-lab: malformed fixture/workingset/lab.json/model inputs crash with
  tracebacks instead of clean refusals
status: Done
assignee: []
created_date: '2026-11-21 12:37'
updated_date: '2026-11-24 18:31'
labels:
  - nlu-lab
dependencies: []
references:
  - scripts/nlu-lab/battery.py
  - scripts/nlu-lab/lab_config.py
  - scripts/nlu-lab/cli.py
  - scripts/nlu-lab/models.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review findings (2026-11-21, Task 8 review of branch worktree-nlu-lab), all verified offline: three nlu-lab input paths crash with raw tracebacks on malformed-but-plausible input instead of the clean refusals the modules promise. (1) scripts/nlu-lab/battery.py load_battery (~line 46): a fixture/workingset row with a typo'd key ({'utterrence': ...}) raises KeyError('utterance'); a bare YAML list item ('- some utterance') raises TypeError on case.get. The escape bypasses _preflight's except (RunRefused, LabConfigError); cli.py's 'None/empty utterance' refusal only ever sees the key-present case. (2) scripts/nlu-lab/lab_config.py LabConfig.load (~line 24): a JSON-valid non-dict lab.json (e.g. the list ["not","a","dict"]) raises AttributeError on data.get, though the docstring promises 'missing/unparseable refuses, never re-inits'; both run and init-skill trace out. _check_workingset already handles this exact shape for workingsets. (3) scripts/nlu-lab/cli.py _language_model (~line 78): re-implements the two-shape discrimination that models._unwrap_or_raise already owns, and {"interactionModel": "oops"} (non-dict value) raises AttributeError instead of RunRefused. Fix all three as refusal paths with tests; for (3), delegate to the models helper (one shape authority) rather than patching the clone.
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
Shipped 2026-11-24 via the merge of worktree-agent-a9f14512e731cf7a6 (worker commit 9677cd9f + review round 1ac70b98). The three sites: (1) battery.py load_battery refuses non-mapping rows and rows without an utterance key via the shared _battery_row shape check (file + 1-based row index + keys in the message; non-string utterances bypass -k so the preflight gate still sees them; non-string expected_intent refuses at load instead of crashing the run report post-save); a fixture file whose YAML top level is not a mapping refuses instead of silently skipping. (2) lab_config.py LabConfig.load refuses a JSON-valid non-dict lab.json with the module's own refusal wording. (3) models.py _unwrap_or_raise is the one shape authority (cli.py already delegated): non-dict wrapper/bare values, wrapper without a languageModel dict, languageModel without an intents list of mappings, and the both-keys-with-garbage shape all refuse with ValueError converted to RunRefused; the bare branch keeps wrapping the whole committed-model doc (dialog belongs inside the wrapper). The --live preflight now applies the same battery-row gates as the lab path (None/empty utterance + 0-rows refusal). 143 lab tests green on main post-merge (PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 python3 -m pytest scripts/nlu-lab/tests -q); the plain CI form is green on CI's clean runner (the local HA-plugin autoload noise reproduces identically on untouched main). Gates: the worker ran /simplify (4 scans) and /code-review high twice in its own session; the orchestrator ran Skill(code-review) medium on the merged delta in this session's transcript and applied all five findings (commit 1ac70b98). JF-956 items 1, 3 and 4 are fixed by the review round; item 2 (lab.json undecodable bytes) remains.
<!-- SECTION:FINAL_SUMMARY:END -->
