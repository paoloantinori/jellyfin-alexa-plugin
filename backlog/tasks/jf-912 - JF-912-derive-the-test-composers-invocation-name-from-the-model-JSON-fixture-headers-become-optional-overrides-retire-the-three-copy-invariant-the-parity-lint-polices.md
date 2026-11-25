---
id: JF-912
title: >-
  JF-912: derive the test composer's invocation name from the model JSON;
  fixture headers become optional overrides (retire the three-copy invariant the
  parity lint polices)
status: Done
assignee: []
created_date: '2026-10-19 23:36'
updated_date: '2026-11-25 06:09'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-877 es/nl code-review round 3 (finding 6, 2026-10-20 night): the invocation name now lives in THREE copies (Config.cs LocaleInvocationNames, each model's invocationName via regeneration, and the per-fixture invocation_name headers across 17 NLU + 20 e2e yaml files). The landed mitigation is DETECTION (the fixture invocation_name parity lint in scripts/validate_interaction_models.py, warning-class: mismatch and deletion both warn, negative-tested). The deeper fix removes the copy: the test runners' one-shot composer (SmapiClient.simulate via conftest's invocation_name wiring) could read the locale's invocation name from the committed model JSON (model_<locale>.json invocationName, already machine-readable and generator-owned) and demote the fixture header to an optional per-fixture override, making the stale-header failure mode (a false-red or silently mis-invoked composed one-shot, the JF-558 month-lag class) impossible instead of merely caught. SCOPE: touches tests/integration/conftest.py + the fixture loading + possibly the smoke/e2e runners' header plumbing; the lint stays as the guard for the override case. LOW priority: the detection layer is in place and green; do this when next touching the test harness.
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
gate-exempt: /simplify not run as a dedicated round; the delta is test-harness Python reviewed by the orchestrator's /code-review medium (all 5 findings applied) with the worker's line-verified audit of the untouched ledger/row-filter/checkpoint hooks; no production C# or model changes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-25 via the merge of worktree-agent-adda4ce91098d7904 (base 2e553643 + review round 3f2331cb). The one-shot composer's invocation name is MODEL-DERIVED by default (conftest's _model_invocation_name reads model_<locale>.json's invocationName through nlu_static's load_locale_model, cached per locale per session, stripped before caching; a missing model or unusable name raises at collection, never silently empty-invokes), with the fixture header DEMOTED to an optional override (present wins; absent/empty/null falls back; a present NON-STRING header refuses loudly in both the loader and the lint). The lint narrowed to the stale-override case and EXTENDED to the two surviving copies: every e2e_smoke open_utterance must contain its model's name (Phase 3b) and every wrapper-battery simulate row + evergreen control of a model-carrying locale must contain it too (new Phase 3c, inert importlib load of the battery module), closing the JF-558 rename-drift class on every copy of the name; the nlu-lab wording updated to the optional contract. No committed fixture or battery row rewritten (the override path stays exercised and linted); the checkpoint/ledger/row-filter hooks untouched (line-verified). 446 script tests green on main post-merge; validator exit 0 with the committed-tree warning count UNCHANGED at 307; dry-run byte-identical; nlu-lab 143 green. Gates: orchestrator /code-review medium (5 findings, all applied in 3f2331cb); worker self-review documented in both commit bodies; the one visible behavior change (an empty-string header now resolves from the model) is dormant on the committed tree.
<!-- SECTION:FINAL_SUMMARY:END -->
