---
id: JF-605
title: >-
  Consolidate the podcast resolve-to-launch tail: three hand-copied variants
  (intent, yes-confirm, APL tap) already diverge on retry, Tell key, and offset
  policy
status: In Progress
assignee: []
created_date: '2026-09-20 19:17'
updated_date: '2026-09-21 05:35'
labels: []
dependencies: []
references:
  - commit eefb09ec
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/AplUserEventHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of eefb09ec (JF-599): the podcast resolve-episodes-then-launch tail exists in three hand-copied variants: PlayPodcastIntentHandler.HandleAsync tail (RetryAsync label 'GetPodcastEpisodes', NoEpisodesInPodcast Tell, offset 0), YesIntentHandler.PlayPodcastEpisode (label 'YesPodcastEpisodes', NoEpisodesInPodcast, offset 0), and the AplUserEventHandler series branch (NO retry, FolderNoPlayableContent on empty, offset from GetResumeOffset). PodcastEpisodeResolver shared only the query construction, not the play sequence. The copies already disagree at birth on retry presence, the empty-result Tell key, and offset policy, which is the same drift failure mode JF-599 was filed to fix (the yes/APL paths had drifted from the intent path). Also fold in the APL-internal duplication the review found: the series and generic folder arms duplicate the resolve epilogue (item reassignment, itemIdStr, NowPlayingQueue, FullNowPlayingItem, LogDebug) with prefixed locals (seriesLocale/seriesUser). A shared PlayLatestEpisode-style helper next to PodcastEpisodeResolver returning the SkillResponse collapses at least the intent and yes copies and gives the APL arm one tail.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One shared helper owns the podcast resolve-to-launch sequence; the three call sites collapse to calls into it
- [ ] #2 The behavioral deltas are reconciled deliberately or preserved with a documented reason: retry presence, empty-result Tell key, offset policy (0 vs GetResumeOffset)
- [ ] #3 The APL site's raw GetItemList is either wrapped in the shared retry or the deviation is documented on the helper
- [ ] #4 Existing PlayPodcast/Yes/APL tests stay green; a new test pins the shared sequence (no-episode Tell key consistency included)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-21 (uncommitted, under the literal gates): PodcastEpisodeResolver.PlayLatestEpisodeAsync is the ONE tail: retry-budgeted query (RetryHelper.ExecuteWithRequestBudgetAsync, the same engine BaseHandler.RetryAsync wraps), NoEpisodesInPodcast empty answer EVERYWHERE (the tap's FolderNoPlayableContent reconciled away, pinned by the new APL key-consistency test), session queue + codec-routed launch (JF-507), and an offsetFor delegate so the APL tap keeps its resume policy (GetResumeOffset on the resolved episode) while intent/yes play fresh at 0. The three call sites collapse: the intent tail, the Yes arm (private method deleted, inline at the switch), the APL series branch (HandleSelectItem now async, retry added per JF-609 which this absorbs). Policies documented on the helper doc. Suite 4187/4187 both TFMs; Release+warnaserror clean.
<!-- SECTION:NOTES:END -->

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
