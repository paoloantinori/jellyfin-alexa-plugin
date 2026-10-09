---
id: JF-853
title: >-
  Backlog marker pathologies: normalize jf-637/643/781 then fold (fold-first
  PROVEN destructive), guard the 4 ENAMETOOLONG files, draft the upstream
  Backlog.md issue
status: To Do
assignee: []
created_date: '2026-10-09 22:44'
labels:
  - tooling
  - tech-debt
  - backlog-hygiene
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the fold sweep's empirical gate (the 3 reverts + the 4 ENAMETOOLONG findings; the sweep's own report, merge a2fb7e6d).

PART 1 - the three deliberately-reverted pathological files (jf-637, jf-643, jf-781): the sweep PROVED that folding their raw orphans would make the next real MCP rewrite WORSE (jf-637 0->5 lines lost, jf-643 0->31, jf-781 3->46), because each carries a marker pathology (an unclosed SECTION:NOTES:ORCHESTRATOR-SIMPLIFY block, a stray nested NOTES:BEGIN, a legacy `<!-- NOTES:` pair) and a second Implementation Notes section flips the MCP from benign pass-through to destructive re-serialization over the pathology. Their orphans remain raw BY EVIDENCE, not omission. Hand treatment, per file: normalize the marker structure FIRST (close the unclosed region, convert/remove the legacy pair, de-nest), re-run the sweep's roundtrip battery (rt_battery.py + census scripts preserved at /var/tmp/fold_sweep/ - copy them into the task's scratch before /var/tmp is wiped) to prove 0-loss, then fold. Do NOT fold before normalizing; that is the whole lesson of the revert.

PART 2 - the four ENAMETOOLONG-class files (jf-724, jf-771, jf-774, jf-782): any backlog CLI/MCP edit on them FAILS with ENAMETOOLONG (regenerated slug exceeds 255 bytes) AND DELETES the file outright - identically at HEAD, a PRE-EXISTING upstream hazard (the destructive rewrite is updateChecklistContent's legacy swallow, per the sweep altitude agent's source read of Backlog.md). Their folds were kept on mechanical evidence only. Actions: (a) never MCP-edit these four files (hand-edit only; the sixth-occurrence memory rule already covers the recovery); (b) draft an upstream issue for the Backlog.md project (both bugs: the destructive rewrite on ENAMETOOLONG, and the raw-tail drop on rewrite) - the draft lives in this task; the maintainer reviews and submits it (outward-facing action, maintainer's call).
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
