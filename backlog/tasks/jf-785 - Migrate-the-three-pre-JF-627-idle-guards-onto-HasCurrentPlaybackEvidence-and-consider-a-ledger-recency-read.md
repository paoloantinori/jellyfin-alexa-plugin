---
id: JF-785
title: >-
  Migrate the three pre-JF-627 idle guards (FavoriteToggle, MediaInfo,
  ApplyRepeatModeAsync) onto HasCurrentPlaybackEvidence; consider a ledger
  recency read as the root fix
status: In Progress
assignee: []
created_date: '2026-10-06 00:00'
updated_date: '2026-10-06 12:00'
labels:
  - tech-debt
  - refactor
dependencies:
  - JF-627
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FavoriteToggleIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/MediaInfoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/ProgressReporter.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-627 /simplify + altitude reviews (2026-10-06), same-turn filing rule.

JF-627 introduced `PlaybackLaunchBuilder.HasCurrentPlaybackEvidence(context, session)` as the ONE current-evidence predicate for the JF-629 idle-guard family, defined as the resolver's own read set (an AudioPlayer token, OR the session's held FullNowPlayingItem, OR its NowPlayingItem DTO). Only the new playlist-edit guard consumes it so far. Three older inline guards remain hand-rolled in a NARROWER two-condition shape (token OR the NowPlayingItem DTO only):

- FavoriteToggleIntentHandler.HandleAsync (~line 92; serves Mark/UnmarkFavorite)
- MediaInfoIntentHandler.HandleAsync (~line 96)
- ProgressReporter.ApplyRepeatModeAsync (~line 821; serves LoopOn/LoopOff/LoopSongOn)

The delta is behavioral, not stylistic: on a session holding FullNowPlayingItem with NO NowPlayingItem DTO and no token, the three DTO-only guards answer their no-media tell even though the shared resolver WOULD resolve the held item (it resolves the full item before the DTO), and the playlist-edit family (post-JF-627) now acts on it. The divergence is an artifact of migration order (those families migrated from DTO-only readers, so their JF-629 guards mirrored their own pre-migration behavior), the same accident class JF-627 was opened to close.

The migration changes those three families' behavior on the full-item-without-DTO shape, so it needs its OWN red proof per family (a pin asserting the handler proceeds and resolves the held item on that shape), not a silent fold. No existing pin covers that shape (verified by grep over the favorite/media/loop suites: only LoopIntentHandlerTests touches FullNowPlayingItem, setting it to null).

ROOT-FIX OBSERVATION (the JF-627 altitude review): the per-family guard layer as a whole is compensation for ONE missing primitive, the ledger's absent recency read. Three families make three compromises over the same missing signal (RateItem acts on the unbounded tail; loop/sleep/speed refuse on it through the belt; favorite/media/playlist-edit answer no-media through the evidence guard). Recording a timestamp beside the route in the DeviceQueueManager last-played ledger and bounding the tail would dissolve the entire guard layer; consider it here before adding any fifth guard.
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

JF-627 GATE-MARKER FINDINGS (2026-10-06): LEG A (the unresolvable-evidence door, finding 1): the idle guard gates on evidence PRESENCE not RESOLVABILITY, so an unresolvable now-playing DTO (item deleted mid-play, or Id == Guid.Empty) lets the resolver's unbounded ledger tail through and 'add this to playlist' ADDS a days-old unrelated item where pre-JF-627 answered NoMediaPlaying; fix shape: guard on resolvable evidence or have guarded families reject ledger-tail answers (displacement-arm answers only). LEG B (the VideoApp-no-token parity, finding 2): HasCurrentPlaybackEvidence has no VideoApp leg, so a video-first device (no AudioPlayer history, no token ever) refuses 'add this' during movies while 'rate this' acts on the same ledger entry; not a regression but the parity boundary is undocumented and unpinned, and video-first 1.0 users hit it on every movie-time 'add this'.