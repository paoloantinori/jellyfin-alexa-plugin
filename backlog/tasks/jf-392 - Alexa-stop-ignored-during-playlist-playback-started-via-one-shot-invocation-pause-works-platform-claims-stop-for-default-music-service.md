---
id: JF-392
title: >-
  'Alexa stop' ignored during playlist playback started via one-shot invocation
  (pause works; platform claims stop for default music service)
status: To Do
assignee: []
created_date: '2026-08-22 08:54'
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
LIVE REPORT 2026-08-22 ~10:52 (user, it-IT Echo Show):

During the Janis Joplin playlist playback (started via one-shot "chiedi a mia collezione di riprodurre la playlist janis joplin greatest hits"), the user said "alexa stop" and it was ignored. Only "alexa pause" stopped the playback.

LOG EVIDENCE:
- 5 tracks played with successful auto-advance (pre-compute working end-to-end)
- At 10:52:25: AMAZON.PauseIntent arrived and stopped playback (offset=142946ms on "Me and Bobby McGee")
- NO StopIntent, NO CancelIntent, NO SessionEndedRequest arrived before the pause
- The play was started from a one-shot invocation (session should have been closed)

This is consistent with the documented platform limitation (CLAUDE.md "Stop/Next/Previous are frequently claimed by the device's default music service"). The JF-387 fix (session attributes) addressed the INTERACTIVE-session variant; this is the one-shot variant where the platform itself routes "stop" elsewhere.

NOTE: the playlist was routed to PlayAlbumIntent (JF-391 NLU issue), which means the initial response was an ALBUM play, not a PLAYLIST play. The handler is the same BuildAudioPlayerResponse path.
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
