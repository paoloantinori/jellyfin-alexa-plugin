---
id: JF-936
title: >-
  nlu-lab: run_battery_rows aborts on non-SmapiError parse failures and swallows
  ERROR-row diagnostics
status: Done
assignee: []
created_date: '2026-11-21 12:37'
updated_date: '2026-11-26 19:01'
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Orchestrator plan (autonomous run, 2026-11-26): worker (own worktree, opus) fixes both review findings in the nlu-lab battery runner: (a) non-SmapiError parser failures (e.g. 'slots': null -> AttributeError inside parse_profile_nlu_result) map to a row (new PARSE_ERROR row status or ERROR status with diagnostics) instead of aborting the run; the parser itself lives in tests/integration/smapi_client.py (the settled extractor home; the lab re-exports it, no lazy imports, no shims) so the null-shape hardening lands there; (b) the JF-406 retry's except no longer discards the exception text: rows carry an error/detail field surfaced into RowResult and the run JSON schema, optional so old run files still load and compare. Tests: null-slots response -> row not abort; ERROR rows retain exception detail (CLI stderr text embedded). Verification: PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 /usr/bin/python3 -m pytest scripts/nlu-lab/tests tests/scripts -q plus a --dry-run battery validation. Dotnet DoD items N/A (python-only).
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE, merged and pushed (worker 47330c5b via merge + gate round 4a6a8d77). Delivered: (1) parse resilience in the settled extractor home tests/integration/smapi_client.py: truthy non-dict selectedIntent now raises the DISTINCT MalformedResponseError (deliberately not a SmapiError subclass; the runner overloads SmapiError as NO_SELECTION) so a response-shape problem maps to a MARKED ERROR row, never an unmarked NO_SELECTION that triage would misread as an NLU-selection regression; null/non-dict slots degrade in both parsers with one shared normalization spelling; AttributeError joined the unexpected-structure catch in parse_nlu_result. (2) run_battery_rows: row-level belt-and-braces catch (unknown future parser shapes cost ONE _error_row, never the run), the JF-406 retry's failure text captured per row (embeds ask CLI stderr, e.g. expired auth), last-attempt-wins, recovered rows carry None; pacing and retry semantics unchanged. (3) RowResult.error optional field through the run JSON, backward compatible (all 80 pre-existing run files load; compare works field-on-one-side). (4) The code-review round's systemic-failure gate: an ALL-ERROR run now fails (rc 1, status failed, run.error naming the first row's diagnostics) instead of printing "run ok ... expectation failures: 0"; partial-ERROR stays ok (boundary pinned). (5) 15 tests (null-slots, malformed-shape ERROR row, persistent-failure e2e rc-1 contract, partial-failure ok boundary, error-text retention, non-dict shape naming, no stale text, schema round trip, old-file compat). Gates: /simplify 4-agent round applied; /code-review high 6/6 applied with the webapp/app.js/summary surfacing filed as JF-975 the same turn (the one consumer-layer gap); 689/689 python tests green. gate-exempt on the dotnet DoD items: python-only change, no C#/model/locale surface (items 1-8 N/A beyond the python suites that ran).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
