---
id: JF-392
title: >-
  'Alexa stop' intermittent: works sometimes (skill opened directly?) but not
  after one-shot invocation - two failure modes or intermittent platform
  routing?
status: To Do
assignee: []
created_date: '2026-08-22 08:54'
updated_date: '2026-08-22 17:34'
labels:
  - bug
  - playback
  - platform-limitation
  - stop
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE REPORT 2026-08-22 (user, it-IT Echo Show):

During Janis Joplin playlist playback (one-shot invocation), "alexa stop" was ignored; only "alexa pause" worked. Log confirmed: zero StopIntent/CancelIntent/SessionEndedRequest arrived.

USER'S UPDATED HYPOTHESIS (2026-08-22, second report):
"stop ogni tanto funziona, ogni tanto no. forse la discriminante e' quando apro la skill direttamente (LaunchRequest), piuttosto che quando uso l'invocazione veloce (chiedi a... di...)"

This is the OPPOSITE direction from the original JF-387 report (where stop failed from the interactive session). After JF-387's fix (session attributes removal from session-ending responses), stop works from the interactive path. Now the user reports it fails from the one-shot path. This suggests either:
1. Two distinct failure modes (one fixed by JF-387, one not yet understood)
2. An intermittent issue not cleanly correlated with launch mode
3. A dependency on what was played before (the "last skill that streamed audio" memory)

CANNOT VERIFY FROM LOGS: all logs from today's testing were truncated by container restarts. No successful-stop examples available for comparison.

CODE ANALYSIS: both paths produce identical AudioPlayer.Play responses (BuildAudioPlayerResponse, shouldEndSession=true, no session attributes after JF-387 fix). No structural difference found.

TEST PROTOCOL for the user (when available):
1. Test A: "apri mia collezione" -> play a song -> "alexa stop" (expected: works per JF-387 fix)
2. Test B: "chiedi a mia collezione di suonare [song]" -> "alexa stop" (expected: fails per this report)  
3. Test C: "chiedi a mia collezione di suonare [song]" -> wait 30s -> "alexa stop" (check if timing matters)
4. Test D: after Test B fails, say "apri mia collezione" -> play same song -> "alexa stop" (check if the LaunchRequest resets the routing)
5. For each test, note the exact time and check logs for what arrived

AMAZON DOCS CONTEXT: "When your skill isn't in an active session but is playing audio, or was the skill most recently playing audio, utterances such as 'Alexa, stop' cause Alexa to send the AMAZON.PauseIntent." The "last skill that streamed audio" memory is lost if another skill or audio service is invoked. Could the one-shot path not properly register as "the last streaming skill" on some device firmware versions?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Check the Alexa app activity card to confirm 'stop' was routed to the default music service (evidence for platform claim)
- [ ] #2 If confirmed platform behavior: no plugin-side fix possible, document as known limitation in the README FAQ (already partially covered by the stop/next entry)
- [ ] #3 If NOT platform behavior (skill was invoked but rejected): investigate the session state on the play response
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
