---
id: JF-936
title: >-
  nlu-lab: run_battery_rows aborts on non-SmapiError parse failures and swallows
  ERROR-row diagnostics
status: To Do
assignee: []
created_date: '2026-11-21 12:37'
labels:
  - nlu-lab
dependencies: []
references:
  - scripts/nlu-lab/smapi_ops.py
  - scripts/nlu-lab/cli.py
  - scripts/nlu-lab/runs.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review findings (2026-11-21, Task 8 review of branch worktree-nlu-lab) in scripts/nlu-lab/smapi_ops.py run_battery_rows: (a) parse-side resilience incomplete: only SmapiError from SmapiClient.parse_profile_nlu_result maps to a NO_SELECTION row; any other parser exception (verified offline: a response with 'slots': null yields None.items() -> AttributeError inside parse_profile_nlu_result) aborts the whole battery, and cmd_run's generic except then writes status=failed with results=[] (only the KeyboardInterrupt path replays partial rows). One malformed response should cost one row, not the run. (b) ERROR rows carry zero diagnostics: the JF-406 retry's 'except Exception: continue' discards the exception text (which embeds the CLI stderr), so a persistent failure such as expired auth produces N ERROR rows with no why, in a tool whose purpose is diagnosis. Keep the failure text (e.g. an error field on the row dict surfaced into RowResult/run schema) and extend the non-SmapiError parser-failure mapping. Tests: null-slots response maps to a row instead of aborting; ERROR rows retain exception detail.
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
