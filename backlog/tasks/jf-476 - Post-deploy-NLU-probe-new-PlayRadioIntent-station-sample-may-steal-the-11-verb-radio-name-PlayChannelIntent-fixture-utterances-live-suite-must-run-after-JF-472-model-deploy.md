---
id: JF-476
title: >-
  Post-deploy NLU probe: new PlayRadioIntent station sample may steal the 11
  "<verb> radio <name>" PlayChannelIntent fixture utterances (live suite must
  run after JF-472 model deploy)
status: To Do
assignee: []
created_date: '2026-09-03 16:39'
labels: []
dependencies: []
references:
  - tests/integration/fixtures/en-US.yaml
  - tests/integration/fixtures/it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayRadioIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-US.json
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-472 final code review. JF-472 adds the first slotted sample to PlayRadioIntent in all 17 locales (e.g. en "play the radio station {station}", it "riproduci la stazione radio {station}", de "Spiele den Radiosender {station}"). Before this change every PlayRadio sample was static, so NO PlayRadio sample could absorb a trailing free-text name; now one can. 11 NLU fixture rows across 11 locales expect the shape "<verb> radio <name>" to route to PlayChannelIntent (channel live-TV playback): en-US/en-GB/en-CA/en-AU/en-IN "Play radio jazz fm" (line 23 each), es-ES/es-MX "Pon la radio jazz fm" (line 23), fr-FR/fr-CA "Lis la radio jazz fm" (line 45), de-DE "Spiele Radio jazz fm" (line 45), it-IT "Riproduci radio rtl" (tests/integration/fixtures/it-IT.yaml line 70). Several share their leading verb + the word "radio" with the new PlayRadio carriers, so the statistical NLU may flip some of these to PlayRadioIntent with station=<name> after the models deploy. The dry-run cannot catch this (profile-nlu tests the saved/deployed model only; the new model is not deployed yet). Consequence of a flip: a user asking for a live-TV radio channel gets the station elicit (nothing playing) or genre-radio mode (something playing) instead of the channel. This is the Amazon-half probe matrix the JF-472 task description already requires ("Probe matrix recorded in the fixture/task for the Amazon half"); this task is its concrete row list.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Live NLU suite (./scripts/run_nlu_tests.sh, per-locale for the 11 locales listed) is run after the JF-472 models deploy to development stage and results recorded here
- [ ] #2 Each '<verb> radio <name>' PlayChannelIntent fixture row is verified to still route to PlayChannelIntent; any row flipped to PlayRadioIntent is triaged: fixture updated with a new expected routing, or the PlayRadio sample carrier requalified (noun added/strengthened), with the on-device impact stated
- [ ] #3 The probe matrix in the JF-472 task description is closed out with these rows
- [ ] #4 Dry-run (--dry-run) still passes after any fixture edits
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
