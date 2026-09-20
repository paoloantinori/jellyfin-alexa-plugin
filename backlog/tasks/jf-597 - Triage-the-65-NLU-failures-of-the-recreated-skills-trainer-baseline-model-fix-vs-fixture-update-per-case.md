---
id: JF-597
title: >-
  Triage the 65 NLU failures of the recreated skill's trainer baseline (model
  fix vs fixture update, per case)
status: To Do
assignee: []
created_date: '2026-09-20 01:46'
labels:
  - nlu
  - interaction-model
  - tech-debt
milestone: Polish
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Post-recreation trainer baseline triage. The 2026-09-20 full NLU re-run (AFTER the catalog recovery + with ER verified alive via ER_SUCCESS_MATCH) shows the failures UNCHANGED: 65 failed / 987 passed vs 66/986 before - the catalog outage was real but separate. The failures are deterministic routing behaviors of the RECREATED skill (Sep-16 config wipe -> new skill -> new trainer state): the old fixtures pinned expectations that relied on the previous skill's trainer quirks/generalizations. Representative confirmed cases (probed 3x deterministic, survived an identical-content rebuild): 'Riproduci star wars' (it) -> PlayNextIntent song-steal; 'Trova/Find inception' (it/en) -> NO_SELECT; 'Suona abbey road' -> NO_SELECT; es-US album slot not filling; MediaInfo media_info_type empty. NOTE the memory nlu_trainer_nondeterminism: per-case fixes need their own 6-10 probe batteries before shipping.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Full failure list captured (pytest cache + /tmp/nlu_rerun_full.log, 2026-09-20 run: 65 failed / 987 passed / 22 skipped)
- [ ] #2 Each failure classified: (a) real user-facing misroute worth a model fix (candidate list from the probes: bare 'Riproduci star wars' class -> PlayNextIntent steal; search verbs 'Trova/Find X' -> NO_SELECT; 'Suona abbey road' bare album -> NO_SELECT) or (b) fixture expectation pinned to the pre-recreation trainer state (generalization-dependent), to update
- [ ] #3 For class (a): model-side fixes follow the anti-pattern rules (carriers/nouns, no bare greedy samples); for class (b): fixtures updated with a comment naming the trainer re-roll
- [ ] #4 Full NLU re-run after the batch: failures reduced to the agreed residual, all remaining failures documented as accepted
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
