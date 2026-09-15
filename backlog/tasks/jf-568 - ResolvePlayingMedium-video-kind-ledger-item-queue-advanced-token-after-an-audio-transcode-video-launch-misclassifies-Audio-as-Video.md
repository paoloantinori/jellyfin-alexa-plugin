---
id: JF-568
title: >-
  ResolvePlayingMedium: video-kind ledger item + queue-advanced token after an
  audio-transcode video launch misclassifies Audio as Video
status: To Do
assignee: []
created_date: '2026-09-15 09:29'
labels:
  - ledger
  - transport
  - edge-case
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review P3 (JF-564, 2026-09-15): a video-kind item (Movie/Episode) launched on the audio-only transcode path (JF-507, screenless or EAC3 audio) puts the movie in the device last-played ledger AND the token; a subsequent PlaybackNearlyFinished enqueue (PostPlayBehavior=AutoPlay radio) moves the token to the radio track WITHOUT recording. BaseHandler.ResolvePlayingMedium (and RepeatIntentHandler's identical videoDisplacedAudio rule) then reads token != ledger + video-kind ledger item as displacement, classifies Video, and Next/Previous answer the "can't navigate video by voice" line while audio is genuinely playing. Narrow window (screenless + episode audio + AutoPlay + transport intent). The trade-off is byte-for-byte the one the RepeatIntentHandler precedent carries (JF-562), so this is consistent intended semantics, not a new bug; file tracks the ledger-design fix (e.g. record enqueued directives too, or persist the launch route per item).
<!-- SECTION:DESCRIPTION:END -->
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
