---
id: JF-560
title: >-
  Experiment: declare Alexa.SeekController on the skill and probe whether a
  custom skill receives seek directives
status: Done
assignee: []
created_date: '2026-09-14 15:03'
updated_date: '2026-09-14 18:04'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-14 19:55 experiment executed as a clean A/B via raw SMAPI (ask CLI hides 400 bodies; the CLI's stored token needed a refresh first; manifest shape fixed using our production manifest's category STREAMING_SERVICE). Pre-research (claudedocs/research_github-device-api-msapi_2026-09-14.md) predicted this outcome: enum closed in every Amazon-generated artifact; the probe closed the last unknown (server-side enforcement).

2026-09-14 20:00 /simplify (adapted, no code diff): consistency agent PASS - the two probe manifests differ only in the interface entry; no secrets; no code. Recorded for the Done gate.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED negative, definitively and cheaply (2026-09-14, A/B probe via SMAPI create-skill-for-vendor): the manifest interface enum IS enforced server-side. Control manifest with only AUDIO_PLAYER was accepted (HTTP 202, throwaway skill amzn1.ask.skill.484cc39b-4106-4b8a-9108-2a090072f950, never enabled); the identical manifest plus {"type":"ALEXA_SEEK_CONTROLLER"} was rejected with INVALID_ENUM_VALUE naming $.manifest.apis.custom.interfaces[1].type (consistency-checked: the two manifests differ ONLY in that entry). Alexa.SeekController/PlaybackController are smart-home discovery-declared device APIs and cannot be declared on a custom skill, so no seek directives can ever arrive to this skill through them. The seek mechanism for non-partner skills remains the offset-republish pattern (confirmed independently by ma-alexa-music-skill), which this plugin already implements for audiobook resume. The throwaway control skill is left dev-stage (deletion is banned by project policy); harmless, never enabled. Evidence: /tmp/jf560_control_manifest.json, /tmp/jf560_probe_manifest.json.
<!-- SECTION:FINAL_SUMMARY:END -->
