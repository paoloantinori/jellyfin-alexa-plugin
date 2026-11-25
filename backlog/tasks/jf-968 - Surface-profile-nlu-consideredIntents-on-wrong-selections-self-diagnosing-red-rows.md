---
id: JF-968
title: >-
  Surface profile-nlu consideredIntents on wrong selections (self-diagnosing red
  rows)
status: To Do
assignee: []
created_date: '2026-11-25 16:05'
updated_date: '2026-11-25 16:19'
labels:
  - nlu-tooling
  - triage
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today smapi_client.parse_profile_nlu_result (tests/integration/smapi_client.py:550) reads consideredIntents ONLY in the no-selection error branch. On a wrong-but-confident selection the runner-up table stays buried in `raw` and never reaches the failure report or the ledger. The Amazon doc (utterance-profiler-api, verified 2026-11-25) documents consideredIntents as a first-class response field. EMPIRICAL SHAPE (Fable's 4 live probes, 2026-11-25 ~17:12): NO scores — each entry is a full Intent object (name, filled slots, ER resolutions); the SAME intent can appear multiple times with different slot segmentations; the selected intent can recur in the list; order/rankean unverified; confident selections often carry an EMPTY list (2 of Fable's 3 greens). CHANGE SHAPE (amended per the Fable consult): (1) capture the FULL hypothesis per considered entry — name, every slot value, ER codes — in the order received, not just names: the slot segmentation IS the diagnostic ('some jazz' vs 'jazz' shows where the carrier cut). (2) The diagnosis dichotomy has a THIRD state: 'expected intent absent from considered' only means vocabulary/coverage when the list is NON-EMPTY; an empty list says nothing. (3) In the ledger this is an ANNOTATION that does NOT enter the seven-verdict precedence of JF-954's triage; after 2-3 live suites, check whether 'expected present in considered' actually separates the steal-vs-miss classes before any triage automation on it. SCOPE SPLIT (sequencing constraint from the consult): the capture part (parser considered field + ledger row schema) lands FIRST because JF-965's baseline protocol needs it; the red-report surfacing and triage-doc updates may ride in parallel with the JF-965 batch. No new SMAPI calls: the data is already in every response we make.
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
