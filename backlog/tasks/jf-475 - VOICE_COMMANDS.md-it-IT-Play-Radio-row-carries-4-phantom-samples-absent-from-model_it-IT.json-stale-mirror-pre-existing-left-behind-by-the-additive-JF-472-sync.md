---
id: JF-475
title: >-
  VOICE_COMMANDS.md it-IT Play Radio row carries 4 phantom samples absent from
  model_it-IT.json (stale mirror, pre-existing, left behind by the additive
  JF-472 sync)
status: In Progress
assignee: []
created_date: '2026-09-03 16:39'
updated_date: '2026-09-04 15:26'
labels: []
dependencies: []
references:
  - VOICE_COMMANDS.md
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-472 final code review. The VOICE_COMMANDS.md it-IT "Play Radio" row (line 642) still lists 4 samples that do NOT exist in model_it-IT.json PlayRadioIntent (verified case-insensitively, and already stale at the pre-JF-472 commit): "Suona la radio" (model has "suona radio"), "Riproduci la radio" (model has "riproduci radio"), "Inizia una stazione radio", and "Suona canzoni come questa". The JF-472 sync was additive-only (10 model-truth samples appended), which is correct for not deleting content, but the row now mixes truth with phantoms. Per the mirror-sync rule in CLAUDE.md anti-pattern #11 area (VOICE_COMMANDS.md mirrors model ground truth), the row should reflect the model. Users reading the doc are being told the skill understands 4 phrases it does not route. Scope: fix the row (and spot-check whether the same phantom pattern exists in other it-IT rows of this file, since the pre-existing row was hand-written). Decide per phantom whether to drop it from the doc or add the sample to the it-IT YAML template (template edit + regen, with NLU fixture awareness).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every sample listed in the VOICE_COMMANDS.md it-IT 'Play Radio' row exists in model_it-IT.json PlayRadioIntent samples (case-insensitive match, or the row adopts the model's exact casing)
- [ ] #2 If a phantom sample is a plausible user phrasing the model should support, it is instead added to the it-IT YAML template and regenerated (decision recorded in the task)
- [ ] #3 A repo-wide case-insensitive sample cross-check (doc rows vs model samples) runs over VOICE_COMMANDS.md and any other stale rows found are fixed in the same change
- [ ] #4 Validation: python3 scripts/generate_interaction_model.py it-IT produces no diff (if template touched) and the doc row matches the regenerated model
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
