---
id: JF-589
title: >-
  Resume restarts from 0 when BOTH position stores are empty at offer time
  (2026-09-18 14:20 incident: restart cadence ate the write; queue files reset;
  context seed did not cover)
status: To Do
assignee: []
created_date: '2026-09-18 12:31'
labels:
  - bug
  - resume
  - incident
  - reliability
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User report 2026-09-18 ~14:20 ('ho riaperto mia collezione, mi ha proposto di riprendere il podcast, ha funzionato ma e' ripartito dall'inizio'). VERIFIED STATE AT THE INCIDENT: (a) Jellyfin was stopped by systemd at 14:11:42 and the container RECREATED (fresh boot at 14:20:54, same auto-update class as the 04:09 incident - a SECOND recreation the same day; the boot at 14:20:54 is in the current container logs); (b) the user's playback of Ep. 1287 (7136e7c6, Morning) STARTED at 14:19:59 (UserData LastPlayedDate, PlayCount=1) - i.e., their resume-yes launched ONE MINUTE before the 14:20:54 boot killed the session; (c) BOTH position stores are EMPTY for the incident item: server UserData PlaybackPositionTicks=0 (the JF-581 write-loss class: the stop never landed, the restart ate it) AND the persisted device-queue store shows NO ItemPositionState entries for any Morning episode (8 queue files touched at 14:11-14:19 all with lastPlayed=none/items=0/posState=0 - whether wiped or freshly-created is NOT determinable: the previous container's logs died with the recreation, so the user's actual LaunchRequest offer chain (the resume_state offset offered) is not recoverable). NET: the offer had nothing to seed from - both fallback legs (UserData -> ItemPositionState) were empty, so offset 0 was the only possible answer; the JF-581 defense could not fire because its INPUT was already lost. OPEN QUESTIONS: (1) what emptied/reset the device queue files at 14:11 (mass empty persists across 8 devices at the stop - a shutdown flush of in-memory state that had already lost the disk content? or fresh files?); (2) why the LaunchRequest AudioPlayer-context seed (device-derived offset, survives restarts by platform contract) did not provide the position - candidates: the context token/offset were stale or absent after the 14:11 session wipe (the JF-567 finding 7 shape), or the offer came from the device-last-played seed with both stores empty; (3) the restart cadence itself (04:09 + 14:11 + 14:20 same day) - the second stop at 14:11:42 was systemd-initiated (journal-verified), trigger unknown (NOT the 04:06 timer slot). NEXT CAPTURE: the CURRENT container logs are fresh - ask the user to listen via the skill, PAUSE (the pause writes the position to BOTH stores - verify the write lands with a log check), then re-open and resume; if the pause-write lands and the resume still starts at 0, the bug is in the offer/read path and the fresh logs will show it end to end. Related: JF-581 (the write-loss defense), JF-588 (the session-miss self-heal; different failure leg), JF-567 finding 7 (the pre-tail token guard shape).
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
