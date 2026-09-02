---
id: JF-387
title: >-
  'Alexa stop' fails to stop playback ONLY when the song was started from inside
  an open interactive session (full invocation 'apri mia collezione');
  one-shot-launched songs stop fine
status: Done
assignee: []
created_date: '2026-08-21 07:21'
updated_date: '2026-08-21 09:20'
labels:
  - bug
  - playback
  - routing
  - platform-limitation
  - session-management
dependencies: []
references:
  - JF-340
  - JF-299
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE REPORT 2026-08-21 ~09:20 (user, it-IT Echo Show):

'Esta Noche' by The Twilight Singers was playing, started from a FULL skill invocation: 'apri mia collezione' followed by in-session interaction (FindSong flow -> keyword -> play). 'Alexa stop' did NOT stop playback. The user's hypothesis (new diagnostic angle, not previously captured): the discriminator is the launch mode. Songs started via quick one-shot invocation ('chiedi a mia collezione...') CAN be stopped afterwards; songs started from inside an open interactive session CANNOT.

EVIDENCE FROM LOGS (2026-08-21 09:15-09:30, minix):
- ZERO Alexa requests reached the skill after the song started: no StopIntent, no CancelIntent, no PauseIntent, no SessionEndedRequest. 'alexa stop' was routed elsewhere before reaching the skill endpoint.
- This matches the documented platform limitation class (CLAUDE.md 'Stop/Next/Previous + content switching during playback -> default music service'; JF-340 open) BUT those docs assume the failure is universal. The user reports it is CONDITIONAL on launch mode, which is new.

KNOWN CONTEXT:
- JF-340 (open, Medium): 'Alexa stop does nothing during playback - investigate genuine-bug vs platform competition (re-surfaced regression)'. This report adds the launch-mode discriminator JF-340 lacked.
- JF-299: shouldEndSession=false on Play responses kept an active session that PREVENTED the Echo from routing 'stop/ferma' to AMAZON.PauseIntent (it sent SessionEndedRequest instead). History: the session-open state affects device routing. The new report may be the same mechanism resurfacing via the interactive-session launch path: a session left open by the FindSong flow + in-session play might leave the device in a state where 'stop' is neither PauseIntent nor routed to the skill.
- The FindSong flow's play response: check what shouldEndSession value the FindSong-initiated play emits vs the one-shot PlaySong path. If the interactive path leaves shouldEndSession=false on a play response, that is the JF-299 anti-pattern recurring through a new door.

WORKAROUNDS (current): pause ('alexa pause' always routes to the active player) or one-shot 'chiedi a mia collezione ferma'.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Reproduce both variants on-device: (a) one-shot invocation 'chiedi a mia collezione di suonare esta noche dei twilight singers' then 'alexa stop'; (b) full invocation 'apri mia collezione' -> in-session play command -> 'alexa stop'. Log whether StopIntent/CancelIntent/PauseIntent or SessionEndedRequest reaches the skill in each.
- [ ] #2 #2 Compare the skill's response shape in the two variants (shouldEndSession value on the play response, session open/closed) and correlate with the routing difference
- [ ] #3 #3 If the session-open variant correlates with stop failure, evaluate whether the play-from-open-session path can close the session differently (e.g. shouldEndSession=true on the play directive) without breaking in-session follow-ups; respect JF-299 (shouldEndSession=false on events is rejected; false on Play responses was harmful)
- [ ] #4 #4 Check the Alexa app activity card for where 'stop' was routed in the failing variant (evidence for platform claim vs skill miss)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
INVESTIGATED + FIX SHIPPED 2026-08-21 (commit 9041e47, deployed to minix; on-device confirmation pending).

ANALYSIS (local + web research):
1. LOCAL: BuildAudioPlayerResponse sets shouldEndSession=true on BOTH paths (one-shot and interactive) - the play response shape was identical EXCEPT one thing: the SessionAttributesInterceptor copied ALL incoming session attributes onto EVERY response, so an interactive FindSong-flow play response carried FindSongSessionData (State:1) and resume_state while the one-shot play carried none. This was the only observable difference between the two play responses.
2. WEB RESEARCH (docs+forums): Amazon delivers 'stop' to the last-streaming skill as AMAZON.PauseIntent when the skill is NOT in an active session; no documented dialog-state persistence across session close; the research's explicit recommendation was to 'verify mechanically that the final elicit-flow response is byte-equivalent to the one-shot response' - a session-ending response silently carrying session state was the one skill-side mechanism consistent with all symptoms.
3. USER HYPOTHESIS CONFIRMED in the sense that we WERE sending dead session data on the terminal play response; whether this alone caused the platform misrouting can only be confirmed on-device (the routing itself is platform-controlled once a clean Play lands - HIGH confidence per docs).

FIX: SessionAttributesInterceptor no longer copies attributes when shouldEndSession=true (session-ending responses are attribute-free; attributes remain preserved on multi-turn elicit responses, which is the interceptor's purpose). The interactive play response is now byte-equivalent to the one-shot one.

VERIFICATION: TDD (guard RED first; two pre-existing tests updated to open-session responses). 2653 green, Release -warnaserror clean, container CI-matching green. Deployed. ON-DEVICE TEST for the user: 'apri mia collezione' -> FindSong flow -> play -> 'alexa stop' should now work; report either way, and if it still fails the AC#1/#2 reproduction matrix becomes the next step (pure platform routing, no further skill-side difference exists to remove).
<!-- SECTION:FINAL_SUMMARY:END -->

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
