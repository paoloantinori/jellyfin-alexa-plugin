---
id: JF-866
title: 'JF-868: pause/cancel on any VideoApp medium ALWAYS speaks VideoStoppedByVoice'
status: Done
assignee: []
created_date: '2026-10-12 11:53'
updated_date: '2026-10-12 15:36'
labels: []
dependencies: []
---

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
Paolo's 2026-10-11 device round caught the JF-847 inconsistency: the stale-STOPPED arm of PauseIntentHandler spoke "cannot pause by voice" while the stop directive it co-sent stopped the video anyway; message and behavior diverged. The state-gate is refuted as a speech discriminator. Fix: the VideoApp-family pause/cancel arm is UNCONDITIONAL (VideoStoppedByVoice + AudioPlayer.Stop directive + session end) for every PlayerActivity; the JF-858 flat-gate fallback and the JF-482 audio pause shape untouched (the aged/evidence-free arm still degrades to Unknown and rides the AUDIO path). CannotPauseVideoByVoice is spoken by no production code (locale keys retained, string surface exempt). Gates: /simplify (3 applied incl. AssertVideoStoppedTell hoist; 1 exempted skip) + /code-review high (3 applied incl. stale-token memory pin; 2 dispositioned, live-TV evidence thinner noted). Tests 5791/5791 BOTH TFMs; Release -warnaserror 0/0. Merged f7ad43c8, deployed to minix AlexaSkill_0.12.1.0, active-DLL verified (17 VideoStoppedByVoice occurrences), config backup /tmp/alexa_config_backup_jf868.xml, pushed main.

DEVICE CONFIRMATION 2026-10-12 17:27 and 17:34 (Echo Show, log-verified): both pause events during video arrived with activity=STOPPED (the stale shape that previously spoke the inconsistent CannotPauseVideoByVoice line; identical frozen offset 178877ms at both timestamps) and the deployed build spoke VideoStoppedByVoice and stopped the video. Message and behavior coincide on both the fresh and stale shapes: the inconsistency Paolo reported is closed on hardware. Also confirmed expected-by-design: the voice stop closes the skill and returns the Show to its home screen (VideoApp has no pause primitive; touch pause is the only true pause), and the tracked position survives for the resume offer.
<!-- SECTION:FINAL_SUMMARY:END -->
