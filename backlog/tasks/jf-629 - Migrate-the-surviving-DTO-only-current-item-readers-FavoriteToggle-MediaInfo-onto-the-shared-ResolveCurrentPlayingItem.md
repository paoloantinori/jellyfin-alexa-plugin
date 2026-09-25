---
id: JF-629
title: >-
  Migrate the surviving DTO-only current-item readers (FavoriteToggle,
  MediaInfo) onto the shared ResolveCurrentPlayingItem
status: To Do
assignee: []
created_date: '2026-09-25 02:09'
labels:
  - refactor
  - tech-debt
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FavoriteToggleIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/MediaInfoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-626 code-review (2026-09-25, finding 6), same-turn filing rule. JF-626 consolidated THREE current-item resolvers into PlaybackLaunchBuilder.ResolveCurrentPlayingItem, but a FOURTH hand-rolled shape survived:

- FavoriteToggleIntentHandler (line ~74): reads ONLY session.NowPlayingItem (the DTO), no AudioPlayer token, no device-ledger displacement. It is the direct sibling of the migrated RateItemIntentHandler in the same write-user-data-on-the-playing-item family. Consequences: after PlaybackStopped clears the session DTO (the documented resume gotcha), "metti questa canzone nei preferiti" answers not-found while "ripeti" and "valuta" resolve the track from the surviving token/ledger; after a VideoApp launch displaces audio, favorite toggles the stale pre-launch item that rating correctly refuses.
- MediaInfoIntentHandler (line ~79): the same DTO-only shape for "what's playing".
- ShuffleOn/ShuffleOff still parse bare GUIDs (the known non-migrated JF-447 sites).

Migration plan: point FavoriteToggle (and MediaInfo, where the token/ledger legs fit its "what is playing" question) at Launch.ResolveCurrentPlayingItem. This depends on the JF-627 decision (the queueManager null contract / whether the playlist-edit-style no-ledger semantics stays), because these handlers currently hold no DeviceQueueManager; decide that first, then migrate with tests pinning the token-survives-PlaybackStopped and displacement shapes.
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
