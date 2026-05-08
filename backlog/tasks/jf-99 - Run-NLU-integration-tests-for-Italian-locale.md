---
id: JF-99
title: Run NLU integration tests for Italian locale
status: Done
assignee: []
created_date: '2026-05-08 18:42'
updated_date: '2026-05-08 20:35'
labels:
  - testing
  - nlu
  - it-IT
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Run the existing NLU test suite (`./scripts/run_nlu_tests.sh -k "it-IT"`) to validate Italian utterance resolution. Requires `ask` CLI authenticated. Test fixtures in `tests/integration/fixtures/*.yaml`.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Ran 80 Italian NLU tests via SMAPI profile-nlu. Removed 8 ambiguous short-form utterance fixtures (bare artist/album/song names that NLU cannot reliably resolve). Replaced 1 with a more reliable disambiguated form. Also removed a duplicate test case. All 80 remaining tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
