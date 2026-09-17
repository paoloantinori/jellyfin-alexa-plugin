---
id: JF-587
title: >-
  Episode screenless degrade for the remaining video-search launch arms:
  PlayVideo, SearchMedia, YesIntent.PlayVideo, PlayRandom still refuse episodes
  on Echo Dots
status: To Do
assignee: []
created_date: '2026-09-17 19:28'
updated_date: '2026-09-17 19:28'
labels:
  - episodes
  - screenless
  - audio-fallback
dependencies: []
references:
  - >-
    backlog/tasks/jf-586 -
    Episode-plays-on-screenless-devices-Echo-Dot-simulator-the-episode-launch-family-degrades-to-the-AudioPlayer-audio-only-route-instead-of-the-screen-required-refusal.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-586 shipped the screenless degrade for the episode launch family via the new PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync chokepoint (adopted by TvNextUpService.LaunchEpisodeAsync covering the NextUp + latest cores, PlayEpisodeIntentHandler's explicit arm, StartOver's Movie-or-Episode branch, ResumeIntent's fallback-4 VideoApp branch, and ContinueWatching's episode arm). The audit found FOUR more handler sites that can launch an EPISODE through the generic VideoApp builder, where the same screen-required refusal still fires on an Echo Dot today because they still call BuildVideoAppLaunchResponseAsync directly: (1) PlayVideoIntentHandler.PlayVideo (its title query includes BaseItemKind.Episode); (2) SearchMediaIntentHandler.PlayItem (the IsVideoType branch, _playableTypes includes Episode); (3) YesIntentHandler.PlayVideo (reached from the video disambiguation confirm, episode-reachable); (4) PlayRandomIntentHandler (its firstItem is Movie or Episode branch). Fix shape per site is mechanical: swap the BuildVideoAppLaunchResponseAsync call for BuildEpisodeLaunchResponseAsync, passing the item, user, the already-resolved GetVideoAppLaunchUrl sourceUrl, the caller's resume ticks (0 on fresh-play arms), and the caller-chosen announce; movies keep the capability refusal inside the builder by construction (the degrade is Episode-only). Add a screenless-episode test per adopted arm following the JF-586 pattern in VideoAppCapabilityGateTests / the handler suites. OUT OF SCOPE unless its own decision: changing the MOVIE refusal on screenless devices (JF-586 deliberately kept it; a movie degrade would burn transcode CPU for a video-first experience, a product call, not a mechanical swap). Deliberately NOT adopted in JF-586 to keep that diff bounded to the task's named family; this task is the same-turn tracker entry for the cut.
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



## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayVideoIntentHandler: an episode hit on a screenless context answers AudioPlayer.Play (no VideoApp directive, no VideoRequiresScreen speech); a movie hit keeps the refusal
- [ ] #2 SearchMediaIntentHandler.PlayItem: same degrade for an episode result on a screenless context
- [ ] #3 YesIntentHandler.PlayVideo: same degrade for a confirmed episode on a screenless context
- [ ] #4 PlayRandomIntentHandler: same degrade when the random pick is an episode on a screenless context
- [ ] #5 Capable-device (VideoApp) responses at all four sites stay byte-identical to today (existing tests pin them; add one pin per site if none)
- [ ] #6 Full dotnet test suite green on both TFMs
<!-- AC:END -->
