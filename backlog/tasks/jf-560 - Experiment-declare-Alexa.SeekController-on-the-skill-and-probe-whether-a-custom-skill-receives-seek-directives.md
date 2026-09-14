---
id: JF-560
title: >-
  Experiment: declare Alexa.SeekController on the skill and probe whether a
  custom skill receives seek directives
status: To Do
assignee: []
created_date: '2026-09-14 15:03'
labels:
  - research
  - alexa-platform
  - audiobook
  - experiment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Research lead (claudedocs/research_echo-show-avs-protocol_2026-09-14.md, finding 6.1): Alexa.SeekController (https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-seekcontroller.html) is a skill-implementable device API with a pre-built voice model ("Alexa, skip thirty seconds on device") for "devices and services that can seek to a specific position". NOBODY has tested whether a custom skill that plays via AudioPlayer can ALSO declare SeekController and receive its directives - it is the only known lead for seek-like behavior without the Music Skill API, i.e. the potential key to the audiobook seek-bar problem (currently impossible: JF-309/Audiobook HLS gives a seek bar via VideoApp only, and AudioPlayer has none). Experiment plan (cheap, read-only): (1) in the Alexa developer console add the Alexa.SeekController interface to the skill manifest (dev stage only, auto-deployed by the next manifest PUT); (2) play an audiobook or long track; (3) say "Alexa, avanti di trenta secondi" / "Alexa, skip thirty seconds"; (4) watch podman logs for any new request type arriving at the endpoint (SeekController directives would surface as unrecognized requests in AlexaSkillController logs - Zero arrival = the interface is not routed to custom skills, close the task with the negative evidence; arrival = new feature door, design the handler). Do NOT ship the manifest change to the release path without a working handler.
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
