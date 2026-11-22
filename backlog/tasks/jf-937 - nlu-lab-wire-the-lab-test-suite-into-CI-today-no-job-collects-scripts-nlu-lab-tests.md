---
id: JF-937
title: >-
  nlu-lab: wire the lab test suite into CI (today no job collects
  scripts/nlu-lab/tests)
status: Done
assignee: []
created_date: '2026-11-21 12:37'
updated_date: '2026-11-21 13:34'
labels:
  - nlu-lab
  - ci
dependencies: []
references:
  - .github/workflows/ci.yml
  - scripts/nlu-lab/tests
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-11-21, Task 8 review of branch worktree-nlu-lab): the scripts/nlu-lab test suite (72 tests at this writing: argv-pinning regression guards such as the ask-smapi-smapi double-prefix class, refusal-before-ask-call contracts, manifest stamping) runs in no CI job. .github/workflows/ci.yml runs 'python3 -m pytest tests/scripts -q' (the Elicit checker harness step) and nothing references scripts/nlu-lab/tests, so the suite executes only where a developer remembers the /usr/bin/python3 + PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 incantation. Without CI, the argv-pinning tests rot silently: a refactor can reintroduce the double-prefix bug with dotnet and validators green, surfacing mid-incident on the live lab. Wiring options: collect the dir from the existing python step, or a dedicated step 'PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 python3 -m pytest scripts/nlu-lab/tests -q' (needs only the pyyaml + pytest deps CI already installs for tests/scripts; keep it hermetic: no network, no ask CLI in CI). Verify the chosen step runs green on ubuntu-latest before closing.
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
Satisfied by the NLU Lab Phase 1 merge (branch worktree-nlu-lab, merge commit on main): the lab-tests step in ci.yml's validate-models job runs python3 -m pytest scripts/nlu-lab/tests -q on every PR/push (106 tests at merge), that job already pip-installs pytest+pyyaml, and the suite imports only stdlib+yaml+pytest (verified by the final gate reviewer). gate-exempt: configuration-only (the task's delta is 2 gitignore lines + the 7-line CI step; reviewed via controller spot-check with git check-ignore and yaml parse evidence, and the whole branch including this hunk passed the final /simplify-angles + code-review gate before merge).
<!-- SECTION:FINAL_SUMMARY:END -->
