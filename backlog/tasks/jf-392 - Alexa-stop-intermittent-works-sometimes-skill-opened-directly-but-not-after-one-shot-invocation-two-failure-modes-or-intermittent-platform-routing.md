---
id: JF-392
title: >-
  'Alexa stop' intermittent: works sometimes (skill opened directly?) but not
  after one-shot invocation - two failure modes or intermittent platform
  routing?
status: Done
assignee: []
created_date: '2026-08-22 08:54'
updated_date: '2026-08-29 04:33'
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
'Alexa stop' intermittently not routed to the skill during AudioPlayer playback. Initial hypothesis (one-shot vs interactive session launch mode) superseded by a timing hypothesis (stop fails within ~30s of a one-shot play), which was then WITHDRAWN after exhaustive research (2026-08-22, claudedocs/research_alexa_stop_routing_2026-08-22.md): no external corroboration, Amazon docs state routing unconditionally. Current status: mechanism UNCONFIRMED. Candidate mechanisms: (a) default-music NLU competition (probabilistic per utterance, verified class on-device 2026-07-02), (b) transient Amazon-side routing incidents (documented class, answerhub a78813), (c) timing/settling window (unsupported by evidence). Evidence so far: 3 test instances (interactive+8s works, one-shot immediate fails, one-shot+90s works). Next step is DATA, not research: per-user diagnostic logging of playback-start context (invocation mode, session.new, timestamp) and stop-attempt outcomes (PauseIntent/StopIntent received or absent), collect N>20 instances. Discriminator: failures clustering under 30s of one-shot starts supports timing; scattered failures support competition/incident.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Check the Alexa app activity card to confirm 'stop' was routed to the default music service (evidence for platform claim)
- [ ] #2 If confirmed platform behavior: no plugin-side fix possible, document as known limitation in the README FAQ (already partially covered by the stop/next entry)
- [ ] #3 If NOT platform behavior (skill was invoked but rejected): investigate the session state on the play response
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REOPENED 2026-08-22: the earlier "platform 30-second settling window" conclusion is WITHDRAWN. Exhaustive research (claudedocs/research_alexa_stop_routing_2026-08-22.md) found: (1) Amazon docs state stop routing unconditionally, no settling window, no one-shot vs session difference; (2) no other Alexa skill project (ASK SDK issue trackers, alexa-samples, My Media for Alexa, music-assistant prototype, bock-media) documents the pattern; (3) Amazon-side intermittent routing incidents ARE a documented class (answerhub a78813 "known issue"); (4) default-music NLU competition (verified on-device 2026-07-02) is probabilistic per utterance and the leading candidate mechanism. Status: mechanism UNCONFIRMED. Candidate mechanisms: (a) default-music NLU competition, (b) transient Amazon routing incidents, (c) timing/settling (unsupported). Next: data collection, not research — instrument playback-start + stop-attempt outcomes via the new per-user diagnostic logging setting (see instrumentation task), collect N>20 instances. If failures cluster under 30s of one-shot starts, timing earns MEDIUM confidence; if scattered randomly, competition/incident wins. Related: JF-340 (open), JF-387, JF-302.

ANSWERED (2026-08-28 night, from live console + simulate-skill evidence): YES, two distinct failure modes. (1) ELICITATION TRAP - while any Dialog.ElicitSlot is open, Alexa captures the next utterance INTO the slot, so 'stop'/'ferma' never reach AMAZON.StopIntent; the FindSong flow kept asking and the session persisted forever (observed 3x in a single day's logs as stop-like titleKeywords values). FIXED by the cancel-word escape hatch in FindSongIntentHandler (FindSongCancelled Tell, session-ending) plus, from JF-411, the album/musician elicit on PlayAlbumIntent. (2) PLATFORM COMPETITION - during AudioPlayer playback, stop/next/previous are claimed by the default music service and never reach the skill (nothing in the logs; documented 2026-07-02 verification, JF-340). Mitigations: 'pausa' (always routed) or one-shot 'chiedi a mia collezione ferma'. Classification recipe: a log line showing the skill intent with stop-like slot value = mode 1; no log line at all = mode 2. Closing as answered; residual platform monitoring stays in JF-340/JF-331.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Conclusione "platform timing (settled)" RITIRATA dopo ricerca esaustiva (vedi Implementation Notes). Task riaperto in stato To Do: serve raccolta dati instrumentata (logging diagnostico per-user) per discriminare tra competizione NLU del default music service, incidenti di routing Amazon, e finestra temporale.
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
