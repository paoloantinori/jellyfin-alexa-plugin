---
id: JF-912
title: >-
  JF-912: derive the test composer's invocation name from the model JSON;
  fixture headers become optional overrides (retire the three-copy invariant the
  parity lint polices)
status: To Do
assignee: []
created_date: '2026-10-19 23:36'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-877 es/nl code-review round 3 (finding 6, 2026-10-20 night): the invocation name now lives in THREE copies (Config.cs LocaleInvocationNames, each model's invocationName via regeneration, and the per-fixture invocation_name headers across 17 NLU + 20 e2e yaml files). The landed mitigation is DETECTION (the fixture invocation_name parity lint in scripts/validate_interaction_models.py, warning-class: mismatch and deletion both warn, negative-tested). The deeper fix removes the copy: the test runners' one-shot composer (SmapiClient.simulate via conftest's invocation_name wiring) could read the locale's invocation name from the committed model JSON (model_<locale>.json invocationName, already machine-readable and generator-owned) and demote the fixture header to an optional per-fixture override, making the stale-header failure mode (a false-red or silently mis-invoked composed one-shot, the JF-558 month-lag class) impossible instead of merely caught. SCOPE: touches tests/integration/conftest.py + the fixture loading + possibly the smoke/e2e runners' header plumbing; the lint stays as the guard for the override case. LOW priority: the detection layer is in place and green; do this when next touching the test harness.
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
