---
id: JF-971
title: >-
  NLU suite and lab: record catalog liveness per session (ER canary) and give
  triage a catalog-state verdict
status: Done
assignee: []
created_date: '2026-11-25 16:24'
updated_date: '2026-11-25 18:00'
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
- [x] #1 Each live suite session and each nlu-lab --live run records, per exercised locale and per wired catalog type, the canary value and whether ER resolved on the catalog authority, at start and at end
- [x] #2 ledger stats does not count a pair toward flips_same_model when the catalog state of the two sessions differs or is unknown; the per-session output shows the catalog state
- [x] #3 triage classifies red rows of catalog-slotted intents in a session with a failed or changed canary under a dedicated verdict placed before FIXTURE_VOCAB, with unit tests for the precedence
- [x] #4 A session without a snapshot records the canary as not-run with a reason; no silent skip
- [x] #5 docs/nlu-static-analysis.md documents the fields and the reading rule
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DoD notes: pure-Python change; dotnet/C# DoD items do not apply. /simplify and code-review high ran inside the worker's transcript (10 simplify findings applied, 5 skipped-as-marginal; 8 code-review findings: 3 code fixes, 2 documented, 1 wording, 1 restructure, 1 out-of-scope filed as JF-973); the orchestrator independently re-ran the full suites (674 green in worktree and on merged main), re-read the load-bearing rules (entry_stable_canonical's stricter Gate-2 comparison, the vacuous-evidence guard, the unknown-probe neutrality) and removed the reviewer scratch file before merge. Deviation from AC #2's letter, in the stricter direction and documented: failed-but-identical pairs are ALSO excluded from flips_same_model (Gate 2: a canary-failed session's flips are not trainer noise even against another failed session).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Catalog ER canary landed on main: scripts/nlu_static/catalog_canary.py is the ONE definition (probe planner over wired catalog types from the snapshot, SMAPI-paced runner, stable/changed/failed/unknown entry semantics per Gate 2); the suite records catalog_state blocks on locale_start and session_end (never silent: every not_run carries a named reason, NLU_CANARY=0 and missing-snapshot both explicit); nlu-lab --live runs carry the same blocks at both endcaps; flips_same_model now requires a proven-clean-equal catalog state on both sessions (all pre-JF-971 sessions read UNKNOWN, so KNOWN_FLAKY stays dormant until NLU_CANARY_SNAPSHOT is armed: the operational consequence is documented); triage gained the eighth verdict CATALOG_STATE after UNSTABLE_BUILD and before FIXTURE_VOCAB with precedence pinned by tests. 53 new tests, 674 total green (re-run by the orchestrator in-worktree and on merged main), suite collection intact. The pre-existing --live deploy fall-through double-run is filed as JF-973.
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
