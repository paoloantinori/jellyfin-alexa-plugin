---
id: JF-854
title: >-
  Orphan-census script becomes a repo tool with --check + test (the only
  raw-tail detector); JF-800's production write-facade residual recorded
status: To Do
assignee: []
created_date: '2026-10-09 22:45'
labels:
  - tooling
  - test-coverage
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the fold sweep (its "commit the census tool" decision point) and the JF-800 completion round (its production write-facade residual). Two small, independent items; one task to avoid tracker noise.

(1) ORPHAN-CENSUS AUDIT SCRIPT as a repo tool: the fold sweep's extractor (census.py/census2.py + the rt_battery roundtrip harness, preserved at /var/tmp/fold_sweep/ - copy into the task's scratch before /var/tmp is wiped) is the ONLY orphan detector. Commit a cleaned, read-only census script under scripts/ (e.g. scripts/audit_backlog_orphans.py) with a --check mode (exit non-zero on real orphan content, so it can ride ci.yml next to validate-locales) + a tests/scripts test pinning its behavior on a fixture file (clean file passes, raw-tail file fails, pathological-marker file handled per the unclosed-to-EOF rule). This turns the seventh-occurrence MCP gotcha into a mechanically guarded invariant: raw tails can never silently accumulate again.

(2) JF-800 RESIDUAL, tracker-landed per the same-turn rule: the parallel-phase guard enforces the static surface ONE level into production code; an uncollected test calling production code that internally WRITES a surface static (one level deep) is unguarded - a documented, deliberate licensing decision from the task (the guard's docs state it honestly). No action requested now; this row exists so the boundary is searchable when a write-vector incident ever appears. Close as wontfix/record if untouched after the 1.0 device round.
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
