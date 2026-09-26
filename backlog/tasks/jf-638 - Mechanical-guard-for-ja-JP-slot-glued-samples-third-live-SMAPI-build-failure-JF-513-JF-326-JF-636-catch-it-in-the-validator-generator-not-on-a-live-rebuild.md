---
id: JF-638
title: >-
  Mechanical guard for ja-JP slot-glued samples: third live SMAPI build failure
  (JF-513, JF-326, JF-636); catch it in the validator/generator, not on a live
  rebuild
status: To Do
assignee: []
created_date: '2026-09-26 16:14'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/ja-JP.yaml
  - scripts/validate_interaction_models.py
  - scripts/generate_interaction_model.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Third occurrence of the same mistake class: a new ja-JP intent shipped with slots glued to surrounding text and only a live SMAPI build failure exposed it. Precedents: JF-513 `{musician}を再生` (InvalidSample, commit 09f0f162), JF-326 `星{star_rating}で` (InvalidCharInSamples, live 17-locale rebuild returned 16/1, commit a4475ff9), JF-636 `速度{speed}` (commit 5b403cf6). The rule is documented only as prose: the templates/ja-JP.yaml header invariant ("Samples use regular ASCII spaces around slot refs ... a slot glued to a particle with no space is rejected") plus per-intent comments at RateItemIntent (line ~355) and SetPlaybackSpeedIntent (line ~448). Neither scripts/validate_interaction_models.py nor scripts/generate_interaction_model.py has any glue check (grep-verified 2026-09-26). Work order: add a mechanical check that flags any ja-JP sample where a slot ref `{...}` is not separated by a regular ASCII space (U+0020) from adjacent non-space characters on each side (both CJK-before `速度{speed}` and particle-after `{speed}にして` shapes; also catch adjacent slot refs `}{`). Two candidate seats, pick deliberately and justify in the task notes: a generator template guard (fails at authoring time, matching the existing guard family in generate_interaction_model.py) and/or a validator check in validate_interaction_models.py (decide error vs warning level explicitly; note the CI validate-models job fails on errors only, and check #10 stays warning BY DESIGN per CLAUDE.md). Guard must not fire on the current tree: all 17 committed models validate clean today (verified).
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
