---
id: JF-562
title: >-
  AMAZON.RepeatIntent is an orphan: no handler, no model declaration in 17
  locales; delivered during playback it kills the session
status: In Progress
assignee: []
created_date: '2026-09-14 20:07'
updated_date: '2026-09-15 02:06'
labels:
  - bug
  - transport
  - interaction-model
  - ux
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Audit 2026-09-14 (matrix intent x medium): AMAZON.RepeatIntent is one of the eleven built-in intents Amazon delivers without invocation name during/after playback, but the plugin has NO handler for it and it is declared in NONE of the 17 interaction models (verified by model scan). Delivered during playback it falls through HandlerSelector to the CouldNotUnderstand Tell and KILLS the session (AlexaSkillController.cs:422-427). Also: 4 locale models (it-IT, de-DE, fr-FR, fr-CA) declare the custom LoopAllOn/LoopAllOff intents INSTEAD of the built-ins (JF-450, IntentNames.cs:40-50) — the loop handlers accept both names so routing is safe, but the built-in delivery path on those locales is unprobed on device. FIX: (1) RepeatIntentHandler: repeat the current item (music: restart current DeviceQueueManager track from 0 via AudioPlayer.Play; audiobook/video/TV: honest per-medium response), register in IntentNames + all 17 model templates + dialog entries as needed; (2) ensure all 11 built-ins are declared in all 17 models (add the missing RepeatIntent declarations; decide LoopAll* vs built-ins per locale with the dual-accept CanHandle kept); (3) graceful per-medium response for delivered-but-uncontrollable cases. Related: the FallbackIntentHandler.cs:62 UnsupportedIntent branch is unreachable dead code for orphan built-ins (CanHandle matches only AMAZON.FallbackIntent) — widen or delete while in the area.
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
