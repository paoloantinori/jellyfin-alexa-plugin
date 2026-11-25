---
id: JF-973
title: >-
  nlu-lab --live deploy fall-through double-runs the battery (JF-944 regression
  class)
status: To Do
assignee: []
created_date: '2026-11-25 17:59'
labels:
  - nlu-tooling
  - bug
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Deferred finding from the JF-971 code-review round (pre-existing, out of that task's scope by its own contract): the nlu-lab CLI's --live path (scripts/nlu-lab/cli.py, the JF-944 fall-through) runs the battery TWICE when a deploy fall-through occurs: double SMAPI cost and the recorded results come from pass 2 while pass 1's rows are discarded. The JF-971 worker left the behavior untouched (run_battery_rows row semantics were out of scope) and corrected its nearby comment to name it as pre-existing. Fix shape: the fall-through should re-run only what the failed deploy invalidates (or reuse pass 1's identity-verified rows when the model sha is unchanged), with a test pinning the single-pass cost. Low urgency (cost, not correctness: results are coherent with pass 2), but it doubles live probe spend on every fall-through.
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
