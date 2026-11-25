---
id: JF-968
title: >-
  Surface profile-nlu consideredIntents on wrong selections (self-diagnosing red
  rows)
status: To Do
assignee: []
created_date: '2026-11-25 16:05'
updated_date: '2026-11-25 16:26'
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
