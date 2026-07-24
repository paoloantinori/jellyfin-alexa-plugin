---
id: JF-279
title: Verify queue manipulation during active playback
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-07-13 20:16'
labels:
  - e2e
  - playback
  - queue
milestone: m-5
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/AddToQueueIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayNextIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Queue manipulation handlers (AddToQueue, PlayNext, ClearQueue, ListQueue) have unit tests for DeviceQueue but no E2E verification during active playback. Need to:
1. Start playback, then "add [song] to queue"
2. Verify "what's in the queue" lists correct tracks
3. Test "play [song] next" — verify it plays after current track
4. Test "clear queue" — verify queue empties but current track continues
5. Verify queue state survives pause/resume cycle
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
