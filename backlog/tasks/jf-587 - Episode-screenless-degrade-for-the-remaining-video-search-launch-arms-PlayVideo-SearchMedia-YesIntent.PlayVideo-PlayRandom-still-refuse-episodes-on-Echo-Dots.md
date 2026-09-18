---
id: JF-587
title: >-
  Episode screenless degrade for the remaining video-search launch arms:
  PlayVideo, SearchMedia, YesIntent.PlayVideo, PlayRandom still refuse episodes
  on Echo Dots
status: Done
assignee: []
created_date: '2026-09-17 19:28'
updated_date: '2026-09-18 05:59'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 59e006ef). The four residual episode-reachable arms adopted the JF-586 screenless degrade: PlayVideoIntentHandler (passes its real resumeTicks - the only resume-bearing arm of the four), SearchMediaIntentHandler.PlayItem, YesIntentHandler.PlayVideo, and PlayRandomIntentHandler (the last three fresh, resumeTicks 0), each swapping BuildVideoAppLaunchResponseAsync for BuildEpisodeLaunchResponseAsync. An episode hit on a screenless device now launches the audio-only AudioPlayer stream instead of the screen-required refusal; movies keep the refusal inside the builder by construction. With this, EVERY Movie/Episode launch site goes through the episode chokepoint: the review classified all remaining direct BuildVideoAppLaunchResponseAsync callers (Recommend gated Movie-only with episodes falling to its AudioPlayer arm; APL carousel taps Movie-only; the channel builder LiveTvChannel-only, where the refusal is correct because the audio route 500s for live sources; the episode builder's own pass-through) and grepped that no hand-built VideoAppLaunchDirective exists outside the builder. Tests: one screenless-episode degrade test per arm (4 new), non-vacuousness proven mechanically by a stash probe (all 4 fail on the old code with an empty directive collection; green restored); fixture honesty verified (TestEpisodeWithStreams drives the real codec probe; aac is decodable so the /Audio/ static-route assertion discriminates against the transcode URL); the resume-bearing degrade itself already covered by the JF-586 offset tests. Gates: /simplify 4-angle + adversarial combined pass - finding 1 applied (the stale 'JF-587 arms still call this directly' doc sentence replaced with the completed state), finding 3 applied (the builder doc's caller-gate claim scoped to the arms that run one; PlayVideo's ungated informational-position semantics are byte-identical to its capable route), finding 2 accepted (SearchMedia's fresh degrade vs its position-bearing speech helper: the documented informational-position semantics its capable route has always had, not a regression), the eager-URL probe kept pre-existing (the URL-first announce gate needs it); efficiency/altitude/reuse CLEAN. Suite 4067/4067 both TFMs, Release 0 warnings. The JF-501 progressive-announce comments at the four sites noted as route-conditional now (nit, builder doc owns the detail).
<!-- SECTION:FINAL_SUMMARY:END -->
