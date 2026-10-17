---
id: JF-905
title: >-
  Validator hardening: QUEUE_CARRIER_NOUNS table + LoopAllOn queue-noun check in
  validate_interaction_models (the bare-loop regression class becomes
  self-enforcing)
status: To Do
assignee: []
created_date: '2026-10-17 16:45'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-904 simplify review (worker handback, could not file from the worktree): the LoopAllOn "samples must name the queue" invariant is enforced today only by fixture pins, not by the validator. A QUEUE_CARRIER_NOUNS table + a carrier check in scripts/validate_interaction_models.py mirroring the existing ALBUM_CARRIER_NOUNS pattern (the JF-459/#11 detection style) would make it self-enforcing across all 17 locales: a future LoopAllOn sample that drops the queue noun (regressing to the bare-loop class JF-902 eliminated) fails validation instead of waiting for a fixture or a live steal. LOW priority: JF-902 did not add it either; the fixtures currently hold the line. When taken: add the table (queue nouns per locale family, sourced from each locale's ListQueue vocabulary exactly as the JF-904 carriers were), the check (warning-level first, matching the #10 precedent unless a real miss shows), and a test in tests/scripts/ mirroring the existing validator tests.
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
