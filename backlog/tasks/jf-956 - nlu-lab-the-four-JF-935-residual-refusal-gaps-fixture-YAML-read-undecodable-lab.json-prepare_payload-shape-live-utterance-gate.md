---
id: JF-956
title: >-
  nlu-lab: the four JF-935 residual refusal gaps (fixture YAML read, undecodable
  lab.json, prepare_payload shape, --live utterance gate)
status: Done
assignee: []
created_date: '2026-11-24 18:19'
updated_date: '2026-11-25 05:39'
labels:
  - nlu-lab
  - robustness
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four adjacent input-validation gaps in scripts/nlu-lab, found and verified by probes during the JF-935 review round (2026-11-24) but deliberately out of that task's three-site scope. All are the same class JF-935 fixed: malformed-but-plausible input crashing with a raw traceback instead of the module's clean refusal. (1) battery.py load_battery fixture loop: malformed fixture YAML or a non-UTF-8 fixture file raises yaml.YAMLError / UnicodeDecodeError raw; the workingset twin refuses the byte-identical corruption, so wrapping the fixture read the same way is the one-line fix. (2) lab_config.py LabConfig.load: a binary/undecodable lab.json raises UnicodeDecodeError because the except only catches json.JSONDecodeError; broadening to UnicodeDecodeError/OSError reuses the same refusal text. (3) models.py prepare_payload: a languageModel without an intents list, or an intent row without a name, crashes with KeyError/TypeError; validate_single_model runs only after prepare_payload so it cannot catch it (either guard in prepare_payload or reorder). (4) cli.py --live: the None/empty-utterance gate exists only in the non-live branch; a None row under --live profiles as utterance=None (pre-existing; the JF-935 docstrings now scope the claim to the non-live gate explicitly).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: trivial-fix scope per the manual's exemption list (two except-clause broadenings, two read wraps, four tests; no production C# or model changes); self-review documented in both commit bodies; the full lab battery re-run green.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SCOPE SHRINK 2026-11-24: items 1, 3 and 4 were fixed by the JF-935 review round (merge of 1ac70b98): (1) the fixture read now refuses non-mapping top levels (the yaml.YAMLError wrapping for MALFORMED fixture YAML text is still open, only the non-dict shape is covered); (3) prepare_payload refuses a languageModel without an intents list of mappings via _validated_intents; (4) the --live preflight applies the same battery-row gates as the lab path. REMAINING: (2) lab_config.py LabConfig.load raising UnicodeDecodeError on binary/undecodable lab.json (the except only catches json.JSONDecodeError; broaden to UnicodeDecodeError/OSError reusing the refusal text), plus the yaml.YAMLError/UnicodeDecodeError wrapping for malformed (as opposed to non-mapping) fixture YAML text from item 1.

Shipped 2026-11-25 via the merge of worktree-agent-af3b7f9871c6ab73b (c491f84a + the workingset symmetry follow-up 1037f7b4). The two remaining decode gaps closed: lab_config.py LabConfig.load refuses a binary/undecodable lab.json (except broadened to JSONDecodeError+UnicodeDecodeError+OSError, refusal text reused verbatim) and battery.py's fixture read refuses malformed YAML and non-UTF-8 fixtures with per-shape messages mirroring the workingset twin; the twin itself now refuses non-UTF-8 workingsets symmetrically (the honest residual the worker flagged, applied on request), with the stale comment rewritten and the WorkingsetRefused docstring carrying the encoding case. Four new offline tests (one per shape incl. the twin), 561 lab+script tests green on main post-merge. gate-exempt: trivial-fix scope (except-clause broadenings and wraps plus tests, Python lab tooling), self-review documented in the commit bodies.
<!-- SECTION:FINAL_SUMMARY:END -->
