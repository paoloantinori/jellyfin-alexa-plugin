---
id: JF-450
title: >-
  Model intents with no handler:
  LoopAllOffIntent/LoopAllOnIntent/RepeatSingleOnIntent declared in 4 locales
  but unreachable (CouldNotUnderstand at runtime)
status: Done
assignee: []
created_date: '2026-09-02 02:50'
updated_date: '2026-09-02 19:36'
labels:
  - interaction-model
  - routing
  - bug
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_de-DE.json
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_fr-FR.json
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_fr-CA.json
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchRoutingTests.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-433 dispatch harness (DispatchRoutingTests.EveryModelCustomIntent_HasARegisteredOwner, allowlisted there until fixed). The interaction models of de-DE, fr-FR, fr-CA and it-IT declare three custom intents that NO handler's CanHandle claims: LoopAllOffIntent, LoopAllOnIntent, RepeatSingleOnIntent. Any utterance matching them routes through the CanHandle loop to the controller's CouldNotUnderstand tell: the user hears "non ho capito" for vocabulary the skill itself published. The other 13 locales do not declare them. Likely leftover loop-repeat vocabulary from a partial feature rollout (LoopOn/LoopOff/LoopSongOn handlers exist and claim AMAZON.LoopOnIntent / AMAZON.LoopOffIntent / LoopSongOnIntent).

Decide per intent: implement a handler (map LoopAllOn/LoopAllOff onto the existing repeat-mode queue vocabulary, and RepeatSingleOn onto the existing LoopSongOn behavior) or remove the intents from the 4 locale models (it-IT via the YAML template in Alexa/InteractionModel/templates/, the others by editing model_<locale>.json directly; then regenerate). Removing is the smaller change if the vocabulary was never live. NOTE: these intents may be reachable by real users today (the model is deployed), so removal must go through the normal model rebuild flow (scripts/generate_interaction_model.py it-IT + rebuild-models deploy).

When fixed, remove the three entries from the unhandledAllowlist in Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchRoutingTests.cs so the test enforces the fixed state.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed with the implement-holders decision, all evidence-based. The three orphaned intents (LoopAllOn/LoopAllOff/RepeatSingleOn) came from a May 2024 model-only localization contribution; JF-438 (the day before) actively curated RepeatSingleOn samples that WIN NLU selection, proving users hit CouldNotUnderstand on live vocabulary, so removal would have orphaned it. The existing loop handlers claim the aliases via the FindSong dual-name idiom (LoopAllOn->RepeatAll, LoopAllOff->RepeatNone, RepeatSingleOn->RepeatOne), deliberately NOT extended to the other 13 locales where the AMAZON.Loop built-ins plus LoopSongOn already cover the semantics (adding them would recreate the JF-438 collision class). Review hardening: the three handlers' shared body collapsed into one BaseHandler.ApplyRepeatModeAsync helper, and the previously latent unguarded new Guid(Token) crash on no-audio-context loop requests (reachable in every locale via the built-ins) now answers the NoMediaPlaying tell. Models unchanged for JF-450 (no deploy needed for this half). Commit 5b8c49d7.
<!-- SECTION:FINAL_SUMMARY:END -->
