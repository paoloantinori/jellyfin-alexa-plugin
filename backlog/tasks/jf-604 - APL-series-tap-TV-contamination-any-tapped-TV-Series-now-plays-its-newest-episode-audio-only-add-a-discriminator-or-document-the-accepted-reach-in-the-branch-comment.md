---
id: JF-604
title: >-
  APL series-tap TV contamination: any tapped TV Series now plays its newest
  episode audio-only; add a discriminator or document the accepted reach in the
  branch comment
status: To Do
assignee: []
created_date: '2026-09-20 19:16'
labels: []
dependencies: []
references:
  - commit eefb09ec
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/AplUserEventHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PodcastEpisodeResolver.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of eefb09ec (JF-599) confirmed end-to-end: AplUserEventHandler's new series branch keys on PodcastEpisodeResolver.IsSeriesShape(folder), which is true for ANY MediaBrowser TV.Series. The browse 'series' category (SlotMappings.cs:66 maps it to BaseItemKind.Series) feeds the APL carousel, and a tap sends carouselTap with the item id into HandleSelectItem, so a REAL TV series tapped on an Echo Show now resolves its newest episode and launches it via BuildAudioPlayerResponse: audio-only playback (ResolveAudioLaunchSource routes an eac3 Episode to the audio-only HLS transcode), video discarded, where the pre-change code answered FolderNoPlayableContent. Mitigating context: the trade-off IS documented in the JF-599 task record for the launch sites, tapped Episode items already played audio-only before this commit (the change converts a refusal into the same audio-only serve), and the in-code comment names only the IlPost surface. Decide and implement one of: (a) a discriminator (e.g. check whether the series' episodes are audio-mediatype, or the library CollectionType profile) so TV series taps keep the previous answer or get a VideoApp launch; or (b) accept and document the behavior change in the branch comment. Note the intent path (PlayPodcastIntentHandler Series fallback) has the same accepted contamination, documented there; whatever is decided should be consistent across the three sites.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A decision is recorded in the code comment at the series branch: either a discriminator is implemented or the TV-tap behavior is explicitly documented as accepted with the reasoning (matching the intent-path comment)
- [ ] #2 If discriminated: a tap on a video-mediatype series does not produce an audio-only launch (unit test with a Video Episode child); a tap on an IlPost-shape series still plays the newest episode
- [ ] #3 If accepted: the APL branch comment names the TV-series reach, not just 'the IlPost carousel surface'
- [ ] #4 Unit test covers the chosen behavior for both a podcast-shaped and a TV-shaped Series
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
