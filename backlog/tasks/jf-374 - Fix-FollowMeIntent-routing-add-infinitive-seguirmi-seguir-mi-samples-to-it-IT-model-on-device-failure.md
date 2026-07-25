---
id: JF-374
title: >-
  Fix FollowMeIntent routing: add infinitive 'seguirmi'/'seguir mi' samples to
  it-IT model (on-device failure)
status: To Do
assignee: []
created_date: '2026-07-25 14:26'
labels:
  - bug
  - nlu
  - follow-me
  - interaction-model
  - it-IT
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FollowMeIntent fails on the natural Italian phrasing 'chiedi a mia collezione di seguirmi' (and the ASR-split 'seguir mi'). Root cause: the it-IT interaction model has only the imperative 'seguimi' sample, no infinitive 'seguirmi'/'seguire'. In Italian, the construction 'chiedi a X di <verb>' requires the infinitive, so every user who phrases follow-me this way hits an unmatched intent and gets an error sound.

VERIFIED 2026-07-25 (on-device, 2 Echos): user said 'chiedi a mia collezione di seguirmi' 3x, always got an error sound. Alexa logs showed ASR consistently delivered 'seguir mi' (split). Even the clean 'seguirmi' would fail: it is not a sample in any of the 17 locale models (confirmed: grep finds zero matches). The only it-IT FollowMe samples are imperative: 'seguimi', 'continua ad ascoltare', 'riprendi da dove ero rimasto', etc.

The same pattern exists elsewhere in the it-IT template (PlayRadio has 'di riprodurre'/'di mettere' infinitive samples), so FollowMe is simply missing its infinitive coverage. ASR also splits the clitic ('seguir mi'), so the split form must be covered too (sample or synonym).

FIX LOCATION: Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml FollowMeIntent samples block (line ~475). Add infinitive forms. For the other 16 locales, audit and add the 'ask <invocation> to follow me' equivalent where missing. Regenerate it-IT via scripts/generate_interaction_model.py; edit JSON directly for the others (per project convention).

NOTE: this is distinct from JF-373 (podcast query) and from JF-270's handler logic. The handler is fine; this is purely an NLU routing gap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 it-IT interaction model includes the infinitive follow-me forms so 'chiedi a <invocation> di seguirmi' routes to FollowMeIntent: add samples 'di seguirmi', 'seguirmi', and the ASR-split variant 'seguir mi' (synonym) so the form Alexa actually hears matches
- [ ] #2 Other locales (en-US et al.) audited for the same gap: the 'ask <invocation> to follow me' / infinitive-equivalent form must be a sample (e.g. en-US 'to follow me')
- [ ] #3 Regenerate model_it-IT.json from the YAML template (python3 scripts/generate_interaction_model.py it-IT); validate with python3 scripts/validate_interaction_models.py
- [ ] #4 Deploy the model + rebuild via the plugin rebuild endpoint (or ask smapi set-interaction-model), then VERIFY via the Utterance Profiler (run_nlu_tests.sh) that 'di seguirmi' / 'seguir mi' routes to FollowMeIntent - this is the layer that failed on-device
- [ ] #5 On-device confirmation (manual, 2 Echos): 'chiedi a mia collezione di seguirmi' no longer returns an error sound and triggers the follow-me transfer
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
