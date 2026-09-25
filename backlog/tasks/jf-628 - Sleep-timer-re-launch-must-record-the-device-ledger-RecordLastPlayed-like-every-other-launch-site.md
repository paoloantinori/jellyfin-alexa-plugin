---
id: JF-628
title: >-
  Sleep timer re-launch must record the device ledger (RecordLastPlayed) like
  every other launch site
status: To Do
assignee: []
created_date: '2026-09-25 02:09'
labels:
  - bug
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SleepTimerIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-626 code-review (2026-09-25, finding 2), same-turn filing rule.

SleepTimerIntentHandler's re-issue directive (around line 120-190) is the one production AudioPlayer.Play built OUTSIDE the BuildAudioPlayerResponse chokepoint (its own comment says so) and it already replicates HALF the chokepoint's bookkeeping (RecordLaunchBase) while skipping the other half: it never calls RecordLastPlayed, so after arming a sleep timer mid-album the device ledger stays pinned on the older launch track while the composite token names the armed track. JF-626 fixed only the READER side (Repeat now parses the composite token through StreamTokenCodec, so the token track wins), but the ledger still lies: ResolvePlayingMedium's doc states the invariant "written by every launch site", and any current-item reader not yet on the shared resolver (FavoriteToggle, MediaInfo, and the JF-619 GetDeviceResumePointer stamp arbitration, which still sees the stale launch-track record after a mid-album arm) reads a ledger that desyncs from the token.

The fix is one line in the sleep handler: RecordLastPlayed(deviceId, itemGuid, LaunchRoute.Audio) beside the existing RecordLaunchBase call, so ledger and token agree and every reader compensates for nothing. Verify with a test that after SleepTimer arms, the device ledger names the armed item; check the JF-619 resume-offer behavior still arbitrates correctly (the sleep re-launch is a real user-initiated play, so pinning the ledger to it is the truthful record).
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
