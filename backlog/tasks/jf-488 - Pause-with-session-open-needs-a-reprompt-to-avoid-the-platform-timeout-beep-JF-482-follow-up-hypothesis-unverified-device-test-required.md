---
id: JF-488
title: >-
  Pause-with-session-open needs a reprompt to avoid the platform timeout beep
  (JF-482 follow-up; hypothesis unverified, device test required)
status: Done
assignee: []
created_date: '2026-09-04 18:51'
updated_date: '2026-09-05 09:55'
labels: []
dependencies: []
references:
  - 'JF-482 (the experiment, the device matrix, and the correction note)'
  - BuildPauseResponse(bool) in BaseHandler.cs
  - PauseIntentHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-482 device matrix (2026-09-04, test b): the PauseKeepsSession flag's open session was closed by the platform with EXCEEDED_MAX_REPROMPTS ~8s after a silent pause response, with an audible error beep. The reprompt hypothesis (that adding a reprompt would prevent the timeout+beep) is UNVERIFIED - see the JF-482 correction note for what was observed vs. what was inferred.

IMPLEMENT (behind the existing PauseKeepsSession flag, no new flag):
1. When PauseKeepsSession=true, the pause response must also carry a reprompt (and likely a minimal OutputSpeech or at minimum the Reprompt object; read the Alexa response contract: a session-open response with neither speech nor reprompt is the shape the platform timed out). The reprompt should be a soft 'dimmi pure quando vuoi riprendere' style localized in all 17 locales (new ResponseStrings key, e.g. PauseSessionReprompt). Consider whether the pause response also needs a brief OutputSpeech (the current design is deliberately silent; if the platform requires speech to accept the reprompt window, the minimal viable form is a very short 'Pausa.' or a non-verbal filler; the experiment will tell).
2. The flag's current byte-identical-off contract must be preserved: with the flag OFF, the pause response is exactly today's (silent, session-ending, no reprompt).

VERIFICATION (device, the JF-482 correction's protocol):
3. Flag ON with the reprompt deployed: pause -> wait 15+ seconds -> observe: (a) does the session still time out with EXCEEDED_MAX_REPROMPTS? (b) does the beep still occur? (c) does the immediate follow-up ('suona jazz' right after the pause) still route in-skill?
4. If the timeout/beep persist WITH the reprompt: the hypothesis is refuted; investigate the alternatives listed in the JF-482 correction (silent-response handling, AudioPlayer.Stop directive interaction, PlaybackStopped session cleanup) and record the findings.

DECISION remains from JF-482: the default stays OFF until the matrix re-runs clean WITH the reprompt.
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
DEVICE VERIFIED 2026-09-05 (Paolo, Echo Show, reprompt deployed): pause response landed with 'Pausa.' + reprompt + open session (corr=af19984c, f2bd6f18, bbfb46f3). The platform did NOT close the session with EXCEEDED_MAX_REPROMPTS: after the 11:43:42 pause it survived 34 seconds and was closed USER_INITIATED (corr=2b69fdcf); zero EXCEEDED_MAX_REPROMPTS in the 3h test window and no beep (yesterday's failure signature is gone). Immediate follow-up after pause routed IN-SKILL (corr=f17e55ad, 'suona jazz' -> PlayRadioIntent, sessionNew=False) and AMAZON.ResumeIntent resumed at the exact pause offset (corr=9cb70b3c, offsetInMilliseconds=14466). The matrix re-ran clean WITH the reprompt: per the JF-482 decision line, the PauseKeepsSession default flips to true.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit 1d387c2c). When PauseKeepsSession=true the pause response carries minimal speech ('Pausa.') plus a reprompt ('Dimmi pure quando vuoi riprendere.'), localized in all 17 locales (PauseSessionSpeech/PauseSessionReprompt). Flag-off responses stay byte-identical to the pre-JF-482 shape, pinned by serialization-equality tests. The locale parameter on BuildPauseResponse(bool, string) is required (compile-time explicitness); the parameterless overload pins its own unread literal. The hypothesis (reprompt prevents the EXCEEDED_MAX_REPROMPTS timeout beep) remains UNVERIFIED pending the device test: enable the flag, pause during playback, wait 15+ seconds, observe timeout/beep/follow-up routing. Flag currently OFF on the live server. Device test card item: the JF-482 matrix test (b) re-run with the reprompt deployed.
<!-- SECTION:FINAL_SUMMARY:END -->
