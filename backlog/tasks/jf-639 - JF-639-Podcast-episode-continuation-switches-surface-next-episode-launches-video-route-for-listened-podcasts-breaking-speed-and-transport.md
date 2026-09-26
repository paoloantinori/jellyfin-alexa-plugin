---
id: JF-639
title: >-
  JF-639 - Podcast episode continuation switches surface: 'next episode'
  launches video route for listened podcasts, breaking speed and transport
status: In Progress
assignee: []
created_date: '2026-09-26 17:25'
updated_date: '2026-09-26 17:36'
labels:
  - bug
  - podcasts
  - device-found
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-636 device round (2026-09-26 19:19-19:21), same-turn rule.

LIVE EVIDENCE: PlayPodcast launched the episode on the AUDIO route (/Audio/{id}/stream, AudioPlayer.PlaybackStarted); Paolo then asked for the NEXT EPISODE, which PlayNextEpisodeIntent routed through the shared TV NextUp core (TvNextUpService.PlayNextUpEpisodeAsync, video-first by design) and launched as VideoApp.Launch with /Videos/{id}/stream; the subsequent 'vai piu veloce' then hit the speed handler's honest VideoApp refusal (correct per design: the video surface is stream-fragile per JF-635 and atempo is audio-only). Net effect: the podcast listening flow silently switches surface on episode advance, and the speed feature (plus every other audio-route nicety: transport robustness, sleep timer) stops working.

ROOT CAUSE: the podcast CONTINUATION path (PlayNextEpisodeIntent, and 'continue watching' sharing the same NextUp core) has no podcast awareness: every Episode-kind item launches via VideoApp regardless of whether the series is a listened podcast (the IlPost shape: Series of video-container Episodes with MP3 inside, per the JF-373/JF-599 notes).

SCOPE: (1) a podcast-series discriminator (library CollectionType=podcasts vs tvshows, or the previous-launch route on the device ledger as a hint); (2) when podcast: continue the AUDIO route - the JF-507 audio-only transcode exists for codec routing and the JF-636 speed endpoint already handles /Videos sources with -map 0:a:0, so the next-episode launch for podcasts should ride the same audio machinery PlayPodcast uses (PodcastEpisodeResolver), keeping the user's standing rate too; (3) the PlayPodcast queue continuation (PlaybackNearlyFinished auto-advance for podcast containers) likely shares the same video-vs-audio fork: audit it under the same discriminator; (4) device-verify the flow: podcast play -> next episode stays audio -> speed change works mid-podcast.
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
