---
id: JF-887
title: >-
  JF-890: loop/shuffle medium gate refuses on a STALE STOPPED video token
  (nothing playing) - must gate on playerActivity, not just the token's medium
status: To Do
assignee: []
created_date: '2026-10-15 11:48'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Paolo's 2026-10-15 12:49 device round (corr 847bcda6): fresh one-shot 'chiedi a mia collezione di attivare loop' with NOTHING playing (app closed, session.new=true, context.AudioPlayer.playerActivity=STOPPED, token=the Sunny Dancer video stopped at offset 106054ms from the earlier test) got the refusal 'Non posso ripetere video, TV o audiolibri da qui. Posso ripendere la musica.' The handler log: 'LoopOn: entered, token=9a114138..., offset=106054ms' then 'LoopOn: Video playing, refusing the repeat-mode write'. ROOT: the loop/shuffle family's medium gate (the JF-564-family PlayingMedium resolution) classifies the medium from the context token REGARDLESS of playerActivity; the device carries the LAST played item in context.AudioPlayer across sessions, so a stale STOPPED video token poisons the gate long after playback ends. He first hit the same class at 2026-10-14 17:16 (that one routed to ShowMoreIntent pre-JF-889; the JF-889 routing fix landed, and now the ROUTING is correct - LoopAllOnIntent arrives - but the HANDLER arm refuses on the dead medium). FIX: the transport-family medium gates (LoopOn/LoopOff/ShuffleOn/ShuffleOff, and audit Pause/StartOver siblings) must distinguish ACTIVE playback from a stale token: only refuse-on-video-medium when playerActivity is PLAYING (or another active state worth treating as active; STOPPED/FINISHED/IDLE/null = nothing playing). When nothing is playing, the honest answer for loop/shuffle is the existing 'nothing playing' line (check the locale keys; NoMediaPlaying exists per JF-875's list) - NOT a medium refusal, and NOT silently writing the flag either (nothing to loop yet; decide: the queue-loop write with no active stream is arguably fine since it presets the next playback, but the honest nothing-playing line is the safer first fix - probe what the write would even target cold). GUARDS: loop during ACTIVE music playback still works (existing pins); loop during ACTIVE video still refuses (existing pins); NEW pins: loop with STOPPED video token + no session -> the nothing-playing line, never the video refusal; same for shuffle. Suite both TFMs, gates, deploy.
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
