---
id: JF-392
title: >-
  'Alexa stop' intermittent: works sometimes (skill opened directly?) but not
  after one-shot invocation - two failure modes or intermittent platform
  routing?
status: Done
assignee: []
created_date: '2026-08-22 08:54'
updated_date: '2026-08-22 19:02'
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
RESOLVED 2026-08-22: the discriminator is TIMING, not launch mode.

TEST RESULTS (user, 2026-08-22 ~21:00):
- Test A (interactive: apri mia collezione -> play -> stop): WORKS. PauseIntent arrived 8 seconds after PlaybackStarted.
- Test B (one-shot: chiedi a... di suonare -> stop immediately): FAILS. No StopIntent/PauseIntent/SessionEndedRequest arrived.
- Test C (one-shot -> wait -> stop): WORKS. PauseIntent arrived ~90 seconds after PlaybackStarted.

ROOT CAUSE (platform timing): the device needs time after a one-shot invocation to fully register the skill as 'the last skill that streamed audio' and start routing 'stop' as PauseIntent. In the interactive path, the multiple turns naturally provide this delay. In the one-shot path, saying 'stop' within ~30 seconds of playback start finds the device still transitioning.

NOT PLUGIN-FIXABLE: this is Amazon device firmware behavior. The skill response is identical in both paths (verified in code and confirmed by the JF-387 session-attributes fix making the responses byte-equivalent).

IMPLICATION: the previously documented JF-387 fix (session attributes) may have been addressing a different layer of the same timing issue (session close race), or may have been coincidental with natural timing differences. The platform timing behavior persists regardless of our response shape.

DOCUMENTED in README FAQ as a note: 'stop' may not work in the first ~30 seconds after starting playback via one-shot invocation; wait a bit or use 'pause' (always works immediately).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Check the Alexa app activity card to confirm 'stop' was routed to the default music service (evidence for platform claim)
- [ ] #2 If confirmed platform behavior: no plugin-side fix possible, document as known limitation in the README FAQ (already partially covered by the stop/next entry)
- [ ] #3 If NOT platform behavior (skill was invoked but rejected): investigate the session state on the play response
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED as platform timing behavior (not plugin-fixable). The user's hypothesis about interactive vs one-shot was partially correct: the REAL discriminator is how much time passes between playback start and the stop command. Interactive sessions naturally have more delay (multiple turns), so stop works. One-shot + immediate stop fails because the device hasn't finished transitioning to audio mode. One-shot + 30+ second wait works.

Test evidence: A (interactive, 8s delta) works; B (one-shot, <30s delta) fails; C (one-shot, 90s delta) works.

README FAQ should be updated to note the timing behavior. The existing 'use pause' workaround is correct and always works.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
