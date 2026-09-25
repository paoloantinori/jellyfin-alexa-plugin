---
id: JF-627
title: >-
  Unify the queueManager null contract between ResolvePlayingMedium and
  ResolveCurrentPlayingItem (or make the playlist-edit no-ledger policy
  deliberate)
status: To Do
assignee: []
created_date: '2026-09-25 01:40'
updated_date: '2026-09-25 02:09'
labels:
  - tech-debt
  - refactor
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaylistEditHandlerBase.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-626 /simplify altitude review (2026-09-25), same-turn filing rule.

JF-626 consolidated the three current-item resolvers into PlaybackLaunchBuilder.ResolveCurrentPlayingItem, which deliberately kept a pre-existing contract divergence: in the SAME class, ResolvePlayingMedium treats queueManager=null as "fall back to Plugin.Instance's DeviceQueueManager" while ResolveCurrentPlayingItem treats null as "disable the ledger arms entirely". The no-ledger policy has exactly one customer (the playlist-edit family: AddCurrentToPlaylist/RemoveCurrentFromPlaylist via PlaylistEditHandlerBase.ResolveCurrentItem) and exists only because that family never took a DeviceQueueManager ctor param; it was an accident RateItem's pre-JF-626 doc even misdocumented. JF-626's mitigation: the queueManager parameter has NO default, so every caller states its choice explicitly.

User-visible inconsistency this perpetuates: during a VideoApp launch of a movie/book/seek-mode song, "repeat this" and "rate this" resolve the displaced item via the ledger, but "add this to playlist X" resolves the stale pre-launch AudioPlayer token item instead.

The decision to make (behavior change, so NOT folded into the JF-626 refactor): either (a) unify the null contract on the Plugin.Instance fallback (ResolvePlayingMedium's shape) so the playlist-edit family gains the full displacement arbitration with no ctor change (DeviceQueueManager is a registered DI singleton; handlers are auto-discovered, or the family can take it by ctor), accepting and testing the playlist-edit behavior change; or (b) keep the divergence deliberately and document it in CLAUDE.md as policy. Also consider a lockstep test enumerating item-kind x route asserting the resolver's displacement decision equals the classifier's non-Audio classification (the JF-625 miss-class guard; the kernel extraction ClassifyLedgerItemKind already shares the ladder, so this is belt-and-braces).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Also fold in the JF-626 review finding 7 decision: the shared resolver's FullNowPlayingItem leg returns the session-held item WITHOUT a library existence check, so a track deleted mid-playback while the session still holds it (and no token resolves) now flows to the playlist-edit family too (pre-JF-626 their DTO path went through GetItemById and answered NoMediaPlaying for a deleted item; the un-guarded semantics was previously scoped to Repeat/RateItem). JF-626 accepts this as the deliberate free-resolution design (the task's fix (b)); if the tracker decision lands on unifying with the ledger arms, decide here whether the deleted-item-held shape also deserves a guard.
<!-- SECTION:NOTES:END -->
