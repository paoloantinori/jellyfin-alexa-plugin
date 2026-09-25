---
id: JF-629
title: >-
  Migrate the surviving DTO-only current-item readers (FavoriteToggle,
  MediaInfo) onto the shared ResolveCurrentPlayingItem
status: Done
assignee: []
created_date: '2026-09-25 02:09'
updated_date: '2026-09-25 10:50'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-629 complete: FavoriteToggle and MediaInfo resolve through the ONE current-item resolver (ResolveCurrentPlayingItem, ctor-deps pattern), fixing the two documented divergences (favorite-after-PlaybackStopped answered not-found; VideoApp displacement toggled/reported the stale item). The review hardened three shapes the migration had quietly changed: idle devices (no token, no session item) answer nothing-playing/MediaNotFound instead of acting on the ledger's days-old persisted item; the displacement answer drops the stale session's position instead of pairing it with the resolved item's runtime (the id-less informational shape keeps it); and the Episode/AudioBook projection paths are pinned. The simplify pass extracted Util.ItemKindLadder (the ONE kind ladder Resume and MediaInfo previously hand-copied), removed the decoy mocks, and collapsed the call-site comments. The JF-627 null-contract question was resolved inline for these handlers (full resolver); the wider unification stays open. Gates: /simplify (2 combined agents) + code-review high (5 findings, all applied incl. 3 new tests) in the orchestrator transcript. Suite 4332x2, deployed, PUSHED.
<!-- SECTION:FINAL_SUMMARY:END -->
