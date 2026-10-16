---
id: JF-893
title: >-
  e2e reliability rows should skip (not fail) under Amazon-side simulate latency
  spikes: per-test timeout after a correct iteration-1 resolution is ambient
  noise
status: To Do
assignee: []
created_date: '2026-10-16 08:57'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Evidence 2026-10-16 ~10:45-10:55: during the fr/es hard-outage window, it-IT simulate still RESOLVED but slowly (22.5s per simulation vs the usual few seconds). The reliability rows (5 identical iterations, e.g. 'riproduci i miei preferiti') then fail with the per-test SMAPI timeout even though routing is CORRECT (iteration 1 resolved to PlayFavoritesIntent and the full-chain e2e row for the same utterance PASSED in the same minutes). The battery's OUTAGE-skipping only engages on hard simulate failures, so an Amazon-side latency spike reddens reliability rows that measure nothing about the skill. SHAPE OF FIX (small, in tests/integration): in the reliability loop, treat a per-test timeout AFTER at least one correctly-resolved iteration as an ambient-noise SKIP with the reason recorded (service slow, N/M iterations resolved), not a FAIL; alternatively scale the per-test budget by the observed iteration-1 latency. Do not touch the functional e2e rows: they keep the strict timeout. Same class as the simulate_outage_per_locale memory: Amazon-side ambient noise must not read as a plugin regression.
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
