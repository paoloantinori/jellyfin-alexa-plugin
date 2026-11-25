---
id: JF-971
title: >-
  NLU suite and lab: record catalog liveness per session (ER canary) and give
  triage a catalog-state verdict
status: To Do
assignee: []
created_date: '2026-11-25 16:24'
labels:
  - nlu-tooling
  - triage
  - process
dependencies: []
references:
  - .claude/skills/nlu-debug-protocol/SKILL.md
  - tests/integration/nlu_ledger_bridge.py
  - scripts/nlu_static/triage.py
  - scripts/nlu-lab/cli.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The results ledger identifies a session by the committed model sha and the live interaction-model eTag (JF-954.5). Neither captures the state of the catalogs: tests/integration/nlu_ledger_bridge.py and conftest.py contain no catalog field (grep verified 2026-11-25). Consequences: (1) the 2026-11-25 catalog deletion and the propagation window after the recovery are invisible to the ledger, so reds from that window read as model evidence; (2) `flips_same_model` counts a flip between a catalog-dead and a catalog-live session as proven trainer nondeterminism when sha and eTag are equal, which feeds KNOWN_FLAKY with a wrong cause (whether the eTag moves when a catalog version is pinned is NOT verified; ER propagation lag after the pin certainly leaves it unchanged, since the model text is the same). JF-684 makes this first-order: an unmatched value on a catalog-backed slot yields no intent selection at all. Wanted: at `locale_start` (suite) and at run start (nlu-lab `--live`), one read-only profile-nlu canary per catalog type wired in that locale, using a value taken from the catalog snapshot, recording per type whether the slot resolved with ER_SUCCESS_MATCH on the catalog authority; repeated at session end like the live identity. Ledger stats exclude pairs whose catalog state differs or is unknown from `flips_same_model`; triage gains a verdict (suggested CATALOG_STATE, placed with UNSTABLE_BUILD before the vocabulary rule) for red rows on catalog-slotted intents in a session where a canary failed or changed. Complements JF-966, which detects deleted catalogs on the plugin side; this task makes the TEST evidence self-describing and also covers the propagation lag that a get-catalog call cannot see. Canary cost: at most six calls per locale per session; skipped with an explicit note when no snapshot is available. Process rule this enables: .claude/skills/nlu-debug-protocol, Gate 2.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each live suite session and each nlu-lab --live run records, per exercised locale and per wired catalog type, the canary value and whether ER resolved on the catalog authority, at start and at end
- [ ] #2 ledger stats does not count a pair toward flips_same_model when the catalog state of the two sessions differs or is unknown; the per-session output shows the catalog state
- [ ] #3 triage classifies red rows of catalog-slotted intents in a session with a failed or changed canary under a dedicated verdict placed before FIXTURE_VOCAB, with unit tests for the precedence
- [ ] #4 A session without a snapshot records the canary as not-run with a reason; no silent skip
- [ ] #5 docs/nlu-static-analysis.md documents the fields and the reading rule
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
