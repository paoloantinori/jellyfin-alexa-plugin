---
id: JF-609
title: >-
  APL series-tap GetItemList violates the resolver's caller contract (no
  RetryAsync): wrap it or correct the contract text
status: Done
assignee: []
created_date: '2026-09-20 19:19'
updated_date: '2026-09-21 06:04'
labels: []
dependencies: []
references:
  - commit eefb09ec
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/AplUserEventHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PodcastEpisodeResolver.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of eefb09ec (JF-599): AplUserEventHandler.cs:199 calls _libraryManager.GetItemList(episodeQuery) raw, while PodcastEpisodeResolver's doc states 'The caller wraps the GetItemList call in its own retry/timeout policy' and the two sibling callers (PlayPodcastIntentHandler 'GetPodcastEpisodes', YesIntentHandler 'YesPodcastEpisodes') wrap it in RetryAsync. A transient DB hiccup during a carousel tap returns an empty list on the first attempt and the handler immediately speaks FolderNoPlayableContent, losing the tap where the siblings would retry. Judged consistent-with-surroundings rather than a regression: the pre-change folder query on this path was equally raw, _libraryManager.GetItemById at line 146 is raw, and HandleSelectItem is fully synchronous (Task.FromResult, no CancellationToken forwarded). This task is likely subsumed by JF-605's shared launch-tail helper; keep it separate only if JF-605 lands without fixing the APL retry.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The resolver's doc and the APL call site agree: either the call is wrapped in RetryAsync (likely via JF-605's shared helper) or the contract text is corrected to describe the actual policy
- [ ] #2 If wrapped: a transient-failure unit test shows the tap path retries instead of immediately speaking FolderNoPlayableContent
- [ ] #3 If documented: the comment explains why the sync tap path runs unwrapped (consistency with GetItemById and the pre-change folder query)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Absorbed into JF-605 (commit 71d4889c): the APL series-tap GetItemList now rides the shared retry-budgeted read inside PodcastEpisodeResolver.PlayLatestEpisodeAsync (RetryHelper.ExecuteWithRequestBudgetAsync, the same engine BaseHandler.RetryAsync wraps); HandleSelectItem threads its cancellation token into it. No residual deviation: the resolver's caller-contract text is gone (BuildLatestEpisodeQuery went private, no bypass possible).
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
