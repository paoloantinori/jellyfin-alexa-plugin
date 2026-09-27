---
id: JF-656
title: >-
  JF-656 - e2e harness: fall back to one-shot when the two-step OPEN simulation
  hits the chronic error class (40/42 of the full-battery failures)
status: To Do
assignee: []
created_date: '2026-09-27 13:31'
labels:
  - e2e
  - harness
  - infrastructure
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the full e2e battery: 40 of 42 failures are the chronic open-simulation error class ('Simulation failed ... An unexpected error occurred' at the two-step OPEN step: 'open/öffne/abre/lance/ouvre/start jellyfin player' under ar-SA, de-DE, es-ES, es-MX, es-US, fr-CA, fr-FR, hi-IN, ja-JP, nl-NL, pt-BR). All two-step smoke tests in those locales failed AT THE OPEN STEP; the corresponding one-shot tests in the same run passed. Known contributing facts: the English invocation name is unreliable under non-English ASR (the english_invocation_broken_nonenglish memory; the two-step open-verb convention exists precisely because of that), the harness already retries the open once (both attempts failed in these runs), and the per-locale simulate outages recur (the simulate_outage_per_locale memory).

THE WORK (harness-level, tests/integration/): when the OPEN step fails with this error class, fall back to running the utterance ONE-SHOT (skip the open; the one-shot form demonstrably works in these locales in the same run) instead of failing the whole two-step test; mark the test result as 'open-fallback' so the report distinguishes a full-chain pass from a fallback pass. Optionally: a pre-run outage gate (one evergreen open probe per locale; locales failing it get the fallback mode upfront instead of burning 20s x N on retries).

VERIFICATION BAR: a rerun of the smoke suites in the affected locales completes with fallback-marked passes instead of the 40 error failures; the it-IT/en-US two-step path (which passes today) is unchanged; dry-run validates fixtures unchanged.
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
