---
id: JF-470
title: >-
  it-IT live NLU suite 21 failures against the 2026-09-03 deployed model
  (triage: JF-438 removal fallout vs fixture staleness vs Amazon ties)
status: To Do
assignee: []
created_date: '2026-09-03 13:49'
labels: []
dependencies: []
references:
  - JF-441 live run log (2026-09-03)
  - JF-438 (Suona+clitic removals)
  - JF-450/451 (SetReminderIntent addition)
  - tests/integration/fixtures/it-IT.yaml
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-441 live verification run (2026-09-03): the it-IT NLU suite is 21 failed / 128 passed against the model deployed 2026-09-03 06:18 UTC. The JF-436 run (2026-09-01) saw only 3 failures, so the regression window is the 09-01/09-02 model commits shipped by the 09-03 deploy: the JF-438 Suona+clitic sample removals, the JF-450/451 SetReminderIntent addition, and catalog version moves (v505 to v511). None of the 21 failures are in the chiamato family (JF-441 handled that).

Worker classification of the 21 (from the run log, needs verification):
- 8 are Amazon 'No selectedIntent' ties on bare artist forms (PlayAlbum vs PlayArtistSongs competition, e.g. 'suona <artist>' shapes)
- 'suona' matrix utterances routing to PlaySongIntent instead of PlayArtistSongs/PlayAlbum (JF-438 removals are the prime suspect: what did Suona+clitic removals take away, and did the fixture matrix get updated in that commit?)
- 'Riproduci star wars' -> PlayNextIntent (?), 'Di suonare disco thriller' -> PlayVideoIntent, 'un album del gruppo michael jackson' -> musician empty

METHOD: run the live it-IT NLU suite (./scripts/run_nlu_tests.sh -k "it-IT" with the env vars; skill id discovered fresh), collect the full failure list with selectedIntent per case, and triage each into: fixture-stale (the 09-01/09-02 commits legitimately changed routing and fixtures were not updated) vs model regression (a removal took a sample family that real utterances need) vs Amazon-side tie (document or work around with more concrete samples per anti-pattern #3). Pay special attention to whether the JF-438 Suona removals have a fixture-coverage gap: those removals were deliberate (see the JF-438 task/commit) but the fixtures may still assert the old routing.

Acceptance criteria:
- Full 21-case triage table (case, selectedIntent, expected, disposition fixture-update vs model-fix vs documented Amazon-side).
- Fixtures updated for the stale cases (existing-entries rule: update with a comment naming this task).
- Model fixes only where a deliberate removal proves wrong; otherwise document.
- Live suite green or the residual failures each carry a documented Amazon-side rationale.
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
