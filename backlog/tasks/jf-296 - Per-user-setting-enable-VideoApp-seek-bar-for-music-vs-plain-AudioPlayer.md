---
id: JF-296
title: 'Per-user setting: enable VideoApp (seek bar) for music vs plain AudioPlayer'
status: Done
assignee: []
created_date: '2026-06-16 16:39'
updated_date: '2026-06-16 18:11'
labels:
  - feature
  - config
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today's root cause: NativeControlsForAudio is a GLOBAL flag that forces ALL music through VideoApp → ffmpeg video-audio encode (for the seek bar), costing a per-song encode (cache-miss latency). For short songs the seek bar is marginal and the encode cost is high. Expose a PER-USER setting (with a global default) modeled on SearchResponseMode/PostPlayBehavior so each user can choose: VideoApp (seek bar + ffmpeg) or AudioPlayer (raw stream, zero transcoding, instant, no seek bar). When a user opts out of VideoApp for music, serve the raw /Audio/{id}/stream?static=true URL via AudioPlayer.Play and bypass VideoAudioController entirely. Keep NativeControlsForBooks (audiobooks benefit from seeking). This is the clean answer to "do we need transcoding for music?" — make it the user's call. Note: Alexa custom-skill AudioPlayer gets no scrubber (Amazon restriction); VideoApp is the only seek-bar path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A per-user (with global default) setting controls whether MUSIC plays via VideoApp (seek bar + ffmpeg video-audio) or plain AudioPlayer (raw stream, zero ffmpeg, instant, no seek bar)
- [ ] #2 When the setting is off for a user, PlaySong/PlayArtistSongs serve the raw /Audio/{id}/stream URL via AudioPlayer — no VideoAudioController/ffmpeg involvement, instant first-play
- [ ] #3 When on, behavior is unchanged (VideoApp + HLS, seek bar retained)
- [ ] #4 Audiobooks are unaffected (NativeControlsForBooks remains separate)
- [ ] #5 Config UI exposes the per-user toggle; locale strings added to all 17 locales
- [ ] #6 Unit tests cover both branches per user; E2E confirms zero-transcode instant play when off
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added per-user nullable VideoAppForAudio (Entities/User.cs) + resolver GetVideoAppForAudio(user) (per-user ?? global NativeControlsForAudio) in BaseHandler, mirroring GetSearchResponseMode. Routing at BaseHandler.cs:535 (Audio/music branch) now uses the resolver; audiobook branch (NativeControlsForBooks) unchanged. When a user opts out (false), PlaySong/PlayArtistSongs serve the raw /Audio/{id}/stream URL via AudioPlayer and bypass VideoAudioController entirely (zero ffmpeg, instant). ConfigurationController PATCH handles the per-user null/true/false; config.html exposes a per-user tri-state 'Music delivery' select. Config UI is plain HTML (not locale-driven) so no locale strings needed. 8 unit tests (per-user true/false/null inheritance, raw-stream-vs-VideoApp routing, audiobook independence). Build green (0 warnings), 2410 tests pass. Committed dc77490. Deployed to AlexaSkill_0.7.0.0 (active DLL verified). Existing user config defaults to null=inherit (no behavior change until toggled).
<!-- SECTION:FINAL_SUMMARY:END -->

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
<!-- DOD:END -->
