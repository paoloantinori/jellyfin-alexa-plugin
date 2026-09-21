---
id: JF-608
title: >-
  Podcast paths log nothing: add the Debug Logging Policy triage data (shape
  branch decision, matched container and episode IDs) to intent and yes-confirm
  paths
status: Done
assignee: []
created_date: '2026-09-20 19:19'
updated_date: '2026-09-20 21:23'
labels: []
dependencies: []
references:
  - commit eefb09ec
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of eefb09ec (JF-599): PlayPodcastIntentHandler.cs contains zero Logger calls anywhere, including the new album-vs-series branch decision, and the fresh YesIntentHandler.PlayPodcastEpisode method logs nothing, in a file whose sibling arms all log matched item IDs (e.g. 'Yes: routing AudioBook item {ItemId} to audiobook playback'). The APL series branch does log ChildName/ChildId. The repo Debug Logging Policy explicitly names 'matched Jellyfin item IDs ... and handler branching decisions' as required triage data: with two storage shapes (AncestorIds vs ParentId), a wrong-episode incident on-device cannot be bisected from podman logs without a redeploy. Mitigation: the intent handler's zero-logging predates the commit (extends an existing gap), but the YesIntentHandler method is fresh code in a logging file. Add Debug logs for the shape decision, matched container, and resolved episode on both paths.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayPodcastIntentHandler logs the branch decision (album vs series shape) and the matched container id/name at Debug level
- [ ] #2 YesIntentHandler.PlayPodcastEpisode logs the confirmed container id and the resolved episode id/name, matching the sibling arms' style
- [ ] #3 A live triage walkthrough is possible from the logs alone: matched IDs + branching decision, per the Debug Logging Policy wording
- [ ] #4 Unit tests unaffected (logging is additive)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-20 (uncommitted, under gates): PlayPodcastIntentHandler logs the series-shape fallback decision (count + first candidate), the matched container (name/id/shape) before the episode query, and the newest episode (name/id); YesIntentHandler.PlayPodcastEpisode logs the confirmed container and the resolved episode with the same shape tag. JF-604's APL reach note also landed in the same diff.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit 72b1989a, /simplify pass recorded in-transcript at 734089bc): PlayPodcastIntentHandler logs the series-shape fallback decision, the matched container (name/id/shape via PodcastEpisodeResolver.DescribeShape), and the newest episode; YesIntentHandler.PlayPodcastEpisode logs the confirmed container and resolved episode in the sibling arms' style. A wrong-episode incident is now bisectable from podman logs alone (AC#3). Deployed to minix with the remediation DLL; CI green on 72b1989a.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
