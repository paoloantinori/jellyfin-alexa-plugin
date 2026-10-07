---
id: JF-808
title: >-
  JF-808 - the playlist play path (ask and confirm) carries no Layer-1 warming gate
status: In Progress
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-806
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-806 round (2026-10-07, same-turn per the
review-recommendation rule; the /code-review high round's finding 1).

JF-806 gated the collection-fetch confirm legs (book, MusicAlbum, artist)
on the warming axis, and JF-807 tracks the book ASK's missing gate, but the
PLAYLIST play path is warming-ungated end to end: PlayPlaylistIntentHandler
and ShufflePlayIntentHandler carry no GuardIndexReady call (absent from
WarmingGateCoverageTests.ExpectedGatedHandlers), and the YesIntent
playlist confirm arm (YesIntentHandler.PlayPlaylist) runs a recursive
unpaged MediaTypes=Audio GetItemList over the playlist folder with no gate
either. During the post-restart index-load window a playlist request (ask
or confirm on an open prompt) hits the cold database inside Alexa's ~8s
window and surfaces as on-device INVALID_RESPONSE: the JF-419 live-incident
class. JF-806 deliberately rescoped the playlist leg out of the MUSIC
axis (its ask does not gate MusicEnabled and playlists are cross-type
always-allowed, so gating only the confirm would CREATE the divergence);
the WARMING axis is a different axis and applies to BOTH sides.

FIX SHAPE: the JF-807 pattern (the PlayAlbumIntentHandler coarse
precedent): decide whether the playlist ask warrants the Layer-1
GuardIndexReady(_artistIndex) entry gate (it needs the IArtistIndex ctor
param threaded into both playlist handlers) and gate the confirm arm to
match whatever the ask answers; add any gated handler to
WarmingGateCoverageTests.ExpectedGatedHandlers; decide the
warming+cross-type interaction (playlists are always-allowed on the
content axis, but the warming axis is about cold-database cost, not
permissions). Mind the confirm-must-match-ask rule when choosing the
arm's ordering.
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
