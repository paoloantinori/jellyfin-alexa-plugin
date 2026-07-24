---
id: JF-316
title: >-
  Extend the YAML interaction-model generator to all 17 locales (kill
  hand-edited JSON drift)
status: To Do
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-07-13 20:17'
labels:
  - maintainability
  - interaction-model
  - tech-debt
milestone: m-7
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - scripts/generate_interaction_model.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Only it-IT is generated from a template (`Alexa/InteractionModel/templates/it-IT.yaml` via `scripts/generate_interaction_model.py`); the other 16 `model_*.json` are hand-maintained (en-US 46KB, it-IT 65KB). Adding one intent means editing 16 large JSON files by hand. The project's own CLAUDE.md documents the direct consequences: "Cross-Locale Drift (8+ incidents)" and "Static Samples Without Slots (7+ incidents)" — recurring regressions the generator already prevents for it-IT but was never extended. Architecture review 2026-07-12 flags this as the second strategic debt alongside the God class.

Fix: extend the YAML-template generator to all 17 locales (per-locale vocabulary files + shared structure), so a new intent/slot is authored once per locale in YAML and generated. Keep the existing validators (validate_interaction_models.py) as the guardrail. This is larger than it looks (each locale needs its own vocabulary curation) — consider a parent task with per-locale-group subtasks, starting with the locales that share structure.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The generator produces model JSON for all 17 locales from per-locale YAML templates
- [ ] #2 Regenerating all locales reproduces the current committed models (or diffs are reviewed and intentional) for a clean baseline
- [ ] #3 validate_interaction_models.py passes on all generated models
- [ ] #4 Documentation updated: adding an intent is a YAML edit + regenerate for every locale, not hand-editing JSON
- [ ] #5 The it-IT generator path continues to work unchanged
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
