---
id: JF-639
title: >-
  JF-639 - Podcast episode continuation switches surface: 'next episode'
  launches video route for listened podcasts, breaking speed and transport
status: Done
assignee: []
created_date: '2026-09-26 17:25'
updated_date: '2026-09-26 19:51'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-26 code-review high findings F2-F6 (F1 applied + pinned; the rest filed per the same-turn rule): F2 RecommendIntentHandler is a 10th episode-launch site bypassing the chokepoint (VideoKinds includes Episode; the plain-audio split at line ~216 loses the standing rate and every chokepoint guarantee) - route its Episode arm through BuildEpisodeLaunchResponseAsync. F3 fail-open leaves real podcast shapes on VideoApp: extensionless CDN URLs (chrt.fm/anchor/buzzsprout redirect shapes), non-http schemes, trailing-slash/encoded forms, missing extensions; local audio path treated asymmetrically (Path arm but no ShortcutPath-local arm); also .aiff/.mka/.weba absent from the list. F4 subsuming the JF-589 arm silently applies the standing PODCAST rate to NON-podcast audio-only episodes (radio-drama rips, commentary tracks) that previously launched at identity - decide and pin (scope the rate to the podcast arm only, or accept + document). F5 test gaps: no screenless-device podcast pin (an arm reorder would silently drop the rate; the degrade defaults identity) and no seeded-queue test; F6 efficiency notes (the streams probe now runs on screenless TV launches; podcast launches pay the caller's discarded VideoApp URL mint) - fold into any JF-637 consolidation.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-639 complete: podcast episode continuation stays on the AUDIO route. The dispatched CollectionType discriminator was unimplementable (Jellyfin has no 'podcasts' type; the production library is tvshows) - implemented instead IsPodcastListenEpisode on content truth (strm-shortcut-to-audio-URL arm + the JF-589 audio-only-streams arm, positive-evidence-only so TV can never misroute), landed at the shared episode-launch CHOKEPOINT so all 9 caller sites (next-episode, latest, continue-watching, resume, start-over, search, yes, PlayEpisode, PlayRandom) answer identically; the podcast arm threads the standing rate, so speed/transport/sleep-timer all work mid-podcast. Root cause sharpened: JF-589 only fired on probed items; unprobed IlPost episodes persist zero streams, so the surface kept flipping even after JF-589. Review gates: /simplify (comment fix applied, 3 nits skipped with reasons) + code-review high (6 findings: F1 applied live - the residual seed now scales by the active rate via StreamMsToContent, pinned by test; F2 Recommend-handler bypass, F3 fail-open shapes (extensionless CDN URLs, non-http schemes), F4 standing rate applied to non-podcast audio-only episodes, F5 arm-order/seed test gaps, F6 probe-cost notes - all filed in the task record for follow-up). Suite 4455x2; deployed. Device round pending: podcast play -> next episode stays audio -> speed works.
<!-- SECTION:FINAL_SUMMARY:END -->
