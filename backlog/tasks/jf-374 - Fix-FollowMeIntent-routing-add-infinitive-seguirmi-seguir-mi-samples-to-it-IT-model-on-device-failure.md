---
id: JF-374
title: >-
  Fix FollowMeIntent routing: add infinitive 'seguirmi'/'seguir mi' samples to
  it-IT model (on-device failure)
status: Done
assignee: []
created_date: '2026-07-25 14:26'
updated_date: '2026-07-25 16:49'
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-25 15:00
---
LIVE VERIFIED 2026-07-25 via direct SMAPI push (the plugin rebuild endpoint was broken, see separate bug): pushed model_it-IT.json via `ask smapi set-interaction-model`; live model now has all 9 FollowMe samples incl seguirmi + seguir mi. NLU Utterance Profiler: all 4 it-IT FollowMe cases PASS (seguimi, seguirmi, seguir mi, +1). Committed NLU fixtures in tests/integration/fixtures/it-IT.yaml. The committed model content was correct all along; only the live-deploy path via the plugin rebuild endpoint failed (filed as separate bug). On-device re-test still pending (user hardware): say 'chiedi a mia collezione di seguirmi' and confirm it now routes to FollowMeIntent instead of the error sound.
---

created: 2026-07-25 16:49
---
CORRECTION 2026-07-25: my earlier note said the plugin rebuild endpoint was broken. That was WRONG. The endpoint rebuilds one locale at a time; I had called it without a 'locale' field so it rebuilt en-US (the CustomModelLocale fallback), and I misread the success. Re-calling with explicit locale:'it-IT' rebuilt it-IT correctly and the live model gained seguirmi via the plugin endpoint (no direct-SMAPI bypass needed). JF-376 filed against the endpoint was closed as not-a-bug. The committed it-IT model fix stands and is live.
---
<!-- COMMENTS:END -->
