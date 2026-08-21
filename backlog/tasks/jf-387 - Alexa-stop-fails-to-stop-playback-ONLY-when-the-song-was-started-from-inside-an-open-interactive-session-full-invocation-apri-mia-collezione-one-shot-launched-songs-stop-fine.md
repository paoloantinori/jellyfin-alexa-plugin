---
id: JF-387
title: >-
  'Alexa stop' fails to stop playback ONLY when the song was started from inside
  an open interactive session (full invocation 'apri mia collezione');
  one-shot-launched songs stop fine
status: To Do
assignee: []
created_date: '2026-08-21 07:21'
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
