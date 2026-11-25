---
id: JF-968
title: >-
  Surface profile-nlu consideredIntents on wrong selections (self-diagnosing red
  rows)
status: Done
assignee: []
created_date: '2026-11-25 16:05'
updated_date: '2026-11-25 17:20'
labels:
  - nlu-tooling
  - triage
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today the considered list is parsed as NAMES ONLY in the lab parser: scripts/nlu-lab/smapi_ops.py `_considered_names` (line ~268) feeds ResultRow.considered_intents (list[str], scripts/nlu-lab/runs.py:40), and the suite's smapi_client.parse_profile_nlu_result (tests/integration/smapi_client.py:550) reads consideredIntents ONLY in the no-selection error branch. The Amazon doc (utterance-profiler-api, verified 2026-11-25) documents consideredIntents as a first-class response field. EMPIRICAL SHAPE (Fable's 4 live probes, 2026-11-25 ~17:12): NO scores; each entry is a full Intent object (name, filled slots, ER resolutions); the SAME intent can appear multiple times with different slot segmentations; the selected intent can recur; order unverified; confident selections often carry an EMPTY list. CHANGE SHAPE (amended per the Fable consult, 2026-11-25 sera): ONE parser, not two — extend the LAB parser (`_considered_names` becomes full-hypothesis extraction: name, every slot value, ER codes, in received order) and widen ResultRow to carry the hypotheses; the SUITE consumes the same function instead of growing its own (one-definition doctrine; the suite's error-branch name list can delegate to it). The slot segmentation IS the diagnostic ('some jazz' vs 'jazz' shows where the carrier cut). The diagnosis dichotomy has a THIRD state: 'expected intent absent from considered' only means vocabulary/coverage when the list is NON-EMPTY; an empty list says nothing. In the ledger this is an ANNOTATION that does NOT enter the seven-verdict precedence; after 2-3 live suites, check whether 'expected present in considered' separates the steal-vs-miss classes before any triage automation. SCOPE SPLIT: the capture part (parser + run/ledger row schema) lands FIRST because JF-965's baseline protocol benefits from full-hypothesis rows (the baseline MECHANISM already exists: two `nlu-lab run --live` + `compare --expect-zero-changes`; this task only enriches what those runs record); red-report surfacing and triage-doc updates may ride in parallel with the JF-965 batch. No new SMAPI calls: the data is already in every response we make.
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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DoD notes: pure-Python change (scripts/nlu-lab + tests/integration + scripts/nlu_static/ledger.py); the dotnet and C#-specific DoD items do not apply; python suites are the test evidence (605 green in-worktree, 621 on merged main, collection 1498 intact, both re-run by the orchestrator). /simplify: four parallel review agents, findings applied (extractor moved home killing the shim/bootstrap/except-all; single extraction per row; dead _considered_names deleted; guard ladder flattened; non-dict slot degrades to None; load_sessions read default; redundant defaultings dropped). code-review high ran twice on the branch; the second round's five findings all applied (multi-value matches, slot-convention documentation, exception-carried extraction, hardened slot walk, stale docstring).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Capture slice landed on main (merge after two review rounds): considered_hypotheses lives module-level in smapi_client (the layer every consumer imports; smapi_ops re-exports it on its existing import, no cycle, no lazy shim, no silent except-all), run files carry the full hypotheses with backward-compatible loading, the ledger row gained a dedicated full-fidelity considered column with the read-side default owned by load_sessions, the suite parser returns considered and the no-selection SmapiError carries the computed list (one extraction per row on every branch), multi-value ER matches keep the whole set (canonical = Values[0] per JF-659, matches = full list for the JF-690/JF-885 class), the selected-slots walk degrades on malformed shapes instead of aborting a battery. 605+ python tests green, suite collection intact (1498). Deferred to the batch-parallel slice (per the scope split): red-report surfacing, triage-doc updates, any considered-based triage automation (evaluate after 2-3 live suites).
<!-- SECTION:FINAL_SUMMARY:END -->
