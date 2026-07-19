---
id: JF-353
title: >-
  Per-user setting to toggle the now-playing announce (read the content name
  aloud on play)
status: Done
assignee:
  - claude
created_date: '2026-07-19 17:11'
updated_date: '2026-07-19 19:00'
labels:
  - ux
  - announce
  - config
  - per-user
dependencies: []
references:
  - JF-349 (video-launch announce)
  - JF-352.2/F2 (audiobook fresh-start announce)
  - JF-352.4/F4 (audio-announce policy)
  - >-
    Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs:107
    (ResumeAnnounceTitle precedent)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
    (GetSearchResponseMode/GetPostPlayBehavior pattern)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The skill speaks a "now playing X" / "In riproduzione X" announce when it launches content. Currently this is unconditional (JF-349 added it to all video-launch paths -- PlayVideo/PlayEpisode/PlayChannel/SearchMedia/AplUserEvent/PlayRandom -- and F2 added it to audiobook fresh-start), with no way for a user to turn it off. The only related config is `ResumeAnnounceTitle` (global, gates the RESUME announce only). Several users (and the maintainer, raised during PR #15 review) find the announce verbose and want it hidden behind a user-exposed setting.

Add a per-user toggle (matching the pattern of SearchResponseMode / PostPlayBehavior / MusicDelivery: a per-user override + a global default) that controls whether the now-playing announce is spoken on play.

Proposed:
- `PluginConfiguration.DefaultAnnounceNowPlaying` (bool, default true -- preserve current behavior).
- Per-user `User.AnnounceNowPlaying` (bool? -- null = use global default), surfaced in the per-user settings UI + the config page table.
- A `GetAnnounceNowPlaying(user)` accessor on BaseHandler (per-user override -> global default), matching `GetSearchResponseMode` / `GetPostPlayBehavior`.
- Gate the announce at the chokepoints: the video-launch announce paths (BuildNowPlayingSpeech / BuildVideoLaunchSpeech call sites in PlayVideo/PlayEpisode/PlayChannel/SearchMedia/AplUserEvent/PlayRandom) + PlayBook fresh-start, set OutputSpeech only when the setting is on. (VideoApp.Launch + AudioPlayer.Play are unaffected -- only the OutputSpeech is suppressed.)
- Default ON (preserve current behavior); users who find it verbose turn it off.

Out of scope / decision: whether this setting also governs audio plays (F4 -- currently silent by default). If the setting should be able to ENABLE audio announces too, that's a separate extension (F4 + locale threading through BuildAudioPlayerResponse); keep this task to gating the existing announces first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Per-user AnnounceNowPlaying setting + global DefaultAnnounceNowPlaying default, following the SearchResponseMode/PostPlayBehavior pattern (GetAnnounceNowPlaying accessor on BaseHandler)
- [x] #2 The now-playing announce on video launches (PlayVideo/PlayEpisode/PlayChannel/SearchMedia/AplUserEvent/PlayRandom) + PlayBook fresh-start is spoken only when the setting is on; suppressed (silent launch) when off -- VideoApp/AudioPlayer directives unchanged
- [x] #3 Config UI: per-user toggle + global default exposed (config.html), persisted across saves
- [x] #4 Default ON (no behavior change for existing users); unit test for on/off; 17 locale response strings unaffected (reuses NowPlayingSsml/NowPlaying)
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-19 19:00
---
DELIVERED (commits be11a68 + 38c7c62): per-user AnnounceNowPlaying (bool?) + global DefaultAnnounceNowPlaying (default true) + GetAnnounceNowPlaying accessor, mirroring the SearchResponseMode/PostPlayBehavior pattern. The announce helpers BuildNowPlayingSpeech + BuildVideoLaunchSpeech take an announceOn param (fresh-play announce suppressed when off; resume announce ungated -- position info). All 10 fresh-play launch sites gated (PlayVideo/PlayEpisode/PlayChannel/SearchMedia/AplUserEvent/PlayRandom via the helpers; YesIntent-Movie via the helper; PlayRadio + Recommend via per-site if -- they compose compound SSML that doesn't fit the helper, altitude-justified). MediaInfo/Repeat (user queries) + the dedicated resume/restart intents are correctly NOT gated. Config UI: global 'Announce Now-Playing on Launch' checkbox + per-user 'Announce now-playing' dropdown (inherit/announce/silent). /simplify (4 agents): clean (efficiency + altitude nothing; helpers/accessor mirror the pattern; low-cost config-surface duplication deferred). /code-review high: found + fixed a real bug -- UpdateGeneralConfig (global-config PATCH) had no handler for DefaultAnnounceNowPlaying so the global toggle didn't persist (silently dropped); added it (38c7c62). + trimmed 2 comment-narration nits. On-device verified: default-on announce (no regression), per-user off -> silent, global off -> persists + silent. Full suite 2536/2536.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Per-user + global toggle for the now-playing announce ("Now playing X" on launch). Per-user AnnounceNowPlaying (bool?) overrides the global DefaultAnnounceNowPlaying (default true, no behavior change for existing users) via a GetAnnounceNowPlaying accessor. The fresh-play announce is gated at all 10 launch sites (via the BuildNowPlayingSpeech/BuildVideoLaunchSpeech helpers' announceOn param + per-site checks for PlayRadio/Recommend); resume/restart announces are intentionally ungated (position info). Config UI: global checkbox + per-user dropdown. /simplify clean; /code-review high found + fixed a global-toggle-persist bug + comment nits. On-device verified (default-on, per-user off, global off+persist). Full suite 2536/2536.
<!-- SECTION:FINAL_SUMMARY:END -->
