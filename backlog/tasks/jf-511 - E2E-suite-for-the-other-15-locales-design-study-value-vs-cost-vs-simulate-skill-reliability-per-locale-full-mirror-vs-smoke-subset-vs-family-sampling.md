---
id: JF-511
title: >-
  E2E suite for the other 15 locales: design study (value vs cost vs
  simulate-skill reliability per locale; full mirror vs smoke subset vs family
  sampling)
status: To Do
assignee: []
created_date: '2026-09-06 19:13'
labels:
  - e2e
  - i18n
  - design-study
dependencies: []
references:
  - JF-510
  - run_e2e_tests.sh
  - the en-US flakiness lesson in CLAUDE.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 direction: today only it-IT (and a small flaky en-US set) have e2e fixtures (simulate-skill based, asserting full responses against the live skill+server); the other 15 locales have NLU fixtures only (profile-nlu, intent/slot routing in isolation). This task is a DESIGN STUDY first, not an implementation order: decide whether and how to build an equivalent full-pipeline suite for the other locales.

QUESTIONS TO ANSWER (study, with live evidence):
1. VALUE: what does an e2e pass catch that the NLU suite does not, per locale? (handler behavior is locale-independent in large part; the locale-specific surface is routing + fill + response strings. If routing is already covered by profile-nlu NLU fixtures, e2e's marginal value per locale may be mostly the response-string layer and cross-layer fill interactions like JF-492/JF-509 strip healing per language family.)
2. COST and RISK: simulate-skill wall-clock x 15 locales (SMAPI delay 1.5s per call; today's 58-test it-IT suite takes minutes; x16 scales accordingly), SMAPI rate limits, and the en-US lesson (competition with built-in skills makes simulate-skill flaky - measure whether de-DE/fr-FR/es-ES simulate-skill is as reliable as it-IT or as flaky as en-US before committing).
3. SHAPE OPTIONS: (a) full mirror of the it-IT suite per locale (highest cost); (b) a SMOKE subset per locale (5-8 tests: one play per media type, one disambiguation, one strip-family case per language family - the JF-509 noun tables are per-language and only e2e can prove the strip heals real fills); (c) locale-family sampling: full e2e for one representative per family (de/fr/es as family heads) + NLU-only for the rest. Recommend one with evidence.
4. PREREQUISITE: the JF-510 it-IT refresh lands first, so the new suite generation starts from a known-good fixture pattern (specific asserts, no response_type:any).
Deliverable: a decision document in the task notes (value/cost/risk per option, recommendation, measured simulate-skill reliability for at least de-DE/fr-FR/es-ES) - then, if the answer is yes, a scoped implementation task with the chosen shape.
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
