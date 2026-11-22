---
id: JF-944
title: >-
  nlu-lab: --live mode (probe the real skill read-only, ground-truth column for
  the catalog divergence)
status: To Do
assignee: []
created_date: '2026-11-22 07:07'
labels: []
dependencies: []
references:
  - scripts/nlu-lab/cli.py
  - scripts/nlu-lab/smapi_ops.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable framework review (2026-11-22), the most impactful suggestion: a 'run --live' mode that profiles the REAL skill with its catalog wiring, writing a normal run file. The lab matrix then gets ground truth as a third column, and the catalog-wiring miss (the stranger-things class: lab green, live red) becomes visible before release instead of after. Profile-nlu is read-only, so this is safe. The lab.json guard must NOT apply to --live since it targets the live skill by definition.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A 'run --live' mode that probes the LIVE skill (read-only, profile-nlu only, no deploy) and writes a normal run file with skill_id and eTag from the live skill
- [ ] #2 The compare command can then show a three-way comparison: lab-before vs lab-after vs live
- [ ] #3 The catalog divergence becomes visible BEFORE release instead of after
- [ ] #4 The lab.json guard does NOT apply to --live (it targets a different skill by definition)
<!-- AC:END -->

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
