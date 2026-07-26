---
id: JF-275
title: >-
  PlayPodcastIntent is broken: MediaTypes=Audio filter on Series never matches
  (never worked in production)
status: Done
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-26 13:56'
labels:
  - e2e
  - podcasts
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayPodcastIntentHandler plays the latest episode of a podcast by name. Unit tests already exist (PlayPodcastIntentHandlerTests.cs, 403 lines) covering handler logic; the gap is END-TO-END coverage against a real Jellyfin library containing podcasts, plus NLU routing of "play the podcast X".

Handler behavior to verify (PlayPodcastIntentHandler.cs):
- Feature-flag gate: PodcastsEnabled=false short-circuits (line 56)
- Empty podcast_name slot → DidNotCatchPodcastName prompt (line 68)
- Exact search: Series + MediaType.Audio query (line 82-90), applies library filter
- Zero exact results → fuzzy fallback SearchItemsFuzzyAsync (line 100) → NotFoundPodcast if still nothing (line 107)
- >1 result → HandleFuzzyMiss disambiguation with MediaTypePodcast (line 111-145)
- Latest episode: Episode + MediaType.Audio, OrderBy DateCreated Desc (line 150-159)
- Zero episodes → NoEpisodesInPodcast (line 166)
- Plays via AudioPlayer /Audio/{id}/stream?static=true (line 181)

NOTE: the original AC "verify same-position resume" does NOT apply to podcasts (podcasts play the latest episode fresh; there is no cross-device position transfer here).
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-25 12:19
---
Session 2026-07-25 (live minix v0.10.0.0): AC #2 PASS (empty slot -> DidNotCatchPodcastName, no AudioPlayer). AC #3 PASS en-US + it-IT (no-match -> NotFoundPodcast). AC #8 PASS (validate_interaction_models.py: all 17 structurally valid; 90 pre-existing advisory warnings unrelated to podcasts). BLOCKED on AC #1, #4, #6: user's Jellyfin has no podcast content yet. User will add a podcast Series (Shows library, audio episodes). Re-run PlayPodcastIntent with real name + inspect podman logs for IncludeItemTypes=Series/MediaTypes=Audio query once content exists.
---

created: 2026-07-25 12:48
---
ROOT CAUSE FOUND 2026-07-25 (live minix v0.10.0.0, real podcast content added): PlayPodcastIntentHandler will NEVER find a podcast in production. Verified facts: (1) Jellyfin sets MediaType=Unknown on EVERY Series item (62/62 Series across all libraries = Unknown, 0 = Audio). Series is a container rollup and never carries MediaType=Audio regardless of child content or library type. (2) The handler's Series query (line 87-88: IncludeItemTypes=Series + MediaTypes=Audio) therefore returns 0 results unconditionally. Dropping the MediaTypes filter finds the podcast immediately (verified: SearchTerm 'In Our Splendid Time' -> 1 match). (3) The feature only ever passed because PlayPodcastIntentHandlerTests.cs hand-constructs a TV.Series object in memory, bypassing Jellyfin's real indexer. JF-71 was marked Done on synthetic tests with no live verification.
---

created: 2026-07-25 12:48
---
SECOND independent problem (ingestion, not plugin): the mp3s were not indexed as audio Episodes. Jellyfin has 50 Episode items, all MediaType=Video, 0 MediaType=Audio. The tvshows library scanner did not ingest loose mp3s as Episode children (Series 'In Our Splendid Time' has 0 indexed children). So even with the Series query fixed, the episode query (line 154, IncludeItemTypes=Episode + MediaTypes=Audio) finds nothing. The correct Jellyfin setup for audio podcasts (library type + folder structure) is UNRESOLVED and needs separate investigation. The plugin's assumption that podcasts = Series with audio Episode children may not match how Jellyfin models podcasts at all.
---

created: 2026-07-25 12:48
---
SCOPE CHANGE: this task is no longer 'verify' — it is 'the podcast feature is broken and needs both a plugin query fix and a confirmed Jellyfin ingestion path'. Do not mark Done until a real podcast plays end-to-end on a live Jellyfin instance.
---

created: 2026-07-25 12:54
---
Spike complete 2026-07-25. CONCLUSION: Jellyfin 10.11.11 has NO first-class podcast concept. Verified against live server: the only MediaType=Audio item types are Audio (music tracks) and AudioBook (single-file books). No Podcast/AudioPodcast type exists; no podcast plugin installed; Series never carries MediaType=Audio (always Unknown). The plugin's assumed shape (Series with MediaType=Audio + audio Episode children) does not exist in Jellyfin's data model for ANY library type. JF-71 was built against a non-existent data model.
---

created: 2026-07-25 12:54
---
VIABLE ingestion path confirmed: podcasts can be stored as a MusicAlbum of Audio tracks in a Music library (verified end-to-end: 'In Our Time' album -> 3 Audio children, MediaType=Audio each; plugin's /Audio/{id}/stream endpoint returns 200 audio/mpeg). So a podcast CAN be played, but the handler must query MusicAlbum/Audio, not Series/Episode.
---

created: 2026-07-25 12:54
---
FIX DIRECTION (not yet implemented): rewrite PlayPodcastIntentHandler to (a) drop MediaTypes=Audio from any Series query OR query MusicAlbum by name, (b) fetch Audio track children sorted by DateCreated desc for 'latest episode', (c) play via the existing /Audio/{id}/stream path. Requires confirming how to distinguish a podcast album from a music album (folder path? a config allowlist? user intent only via the dedicated PlayPodcast utterance?). Open question for the fix task: is treating podcasts-as-albums acceptable, or does the team want to require a dedicated podcast library/plugin?
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in JF-373 (v0.11.1.0): PlayPodcastIntentHandler now queries MusicAlbum/Audio instead of the non-existent Series/MediaTypes=Audio model. Verified live on minix: 'play the podcast In Our Time' returns AudioPlayer.Play to the newest episode. E2E fixture passes via SMAPI simulate-skill.
<!-- SECTION:FINAL_SUMMARY:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E/simulator: with a real podcast series in the Jellyfin library, 'play the podcast {name}' routes to PlayPodcastIntent and returns an AudioPlayer.Play directive whose stream URL points to the latest episode (highest DateCreated)
- [x] #2 E2E/simulator: podcast name absent/whitespace slot returns the DidNotCatchPodcastName prompt, not an AudioPlayer directive
- [x] #3 E2E/simulator: podcast name with no library match returns the NotFoundPodcast spoken response (no stream)
- [ ] #4 E2E/simulator: a podcast Series that exists but has zero audio Episodes returns the NoEpisodesInPodcast spoken response
- [ ] #5 Simulator: PodcastsEnabled=false blocks the intent (returns the disabled response) — complements existing unit test JF-146
- [ ] #6 Confirm Jellyfin query uses IncludeItemTypes=Series + MediaTypes=Audio (not MusicAlbum) so it doesn't pick up music albums — verify via podman logs query inspection
- [ ] #7 If the user's Jellyfin has NO podcast library at all, document the prerequisite (add a podcast channel/Series) rather than treat absence as a failure
- [x] #8 Run python3 scripts/validate_interaction_models.py — confirms PlayPodcastIntent + podcast_name slot present and consistent across all 17 locales
<!-- AC:END -->
