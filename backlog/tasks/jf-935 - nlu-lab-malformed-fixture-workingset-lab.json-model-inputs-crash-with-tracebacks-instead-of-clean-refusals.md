---
id: JF-935
title: >-
  nlu-lab: malformed fixture/workingset/lab.json/model inputs crash with
  tracebacks instead of clean refusals
status: In Progress
assignee: []
created_date: '2026-11-21 12:37'
updated_date: '2026-11-24 16:41'
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
