---
id: JF-341
title: >-
  PlayAlbum multi-match disambiguation is inert (HandleFuzzyMiss auto-accepts) —
  real disambiguation needs distinct-name check + AskFirstMatch
status: To Do
assignee: []
created_date: '2026-07-13 18:20'
updated_date: '2026-07-13 20:16'
labels:
  - album
  - disambiguation
  - fuzzy
  - ux
  - follow-up
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by /code-review high round 3 (2026-07-13). The b12cf5c fix #3 (switch PlayAlbum's fuzzy fallback from FindBestMatchWithScore to RankMatches to enable disambiguation) is INERT: when albums.Count > 1, HandleFuzzyMiss (BaseHandler.cs:1263) re-runs FindBestMatchWithScore and AUTO-ACCEPTS the best match at score >= GetDefaultThreshold (line 1280). Since RankMatches pre-filtered at >= 60 >= the threshold, the AskFirstMatch disambiguation path (requires score < SuggestionThreshold=40) is UNREACHABLE. So multi-match albums still silently play the best — RankMatches just added wasted CPU. Reverted in this session (RankMatches → FindBestMatchWithScore); the RankMatches maxLenDiff addition (1a59dd5) was also removed (RankMatches now unused).

The REAL fix requires changing how multi-match is handled: when the album resolution produces multiple DISTINCT-NAME albums, bypass HandleFuzzyMiss's auto-accept and go to AskFirstMatch (prompt the user). The subtlety: same-name duplicates (e.g. two "Jazz Cafe" disc-albums) should NOT prompt (useless "Jazz Cafe or Jazz Cafe?") — auto-play; different-name collisions (e.g. "Greatest Hits" by Queen vs ABBA) SHOULD prompt.

Verified context: the /code-review traced HandleFuzzyMiss's auto-accept logic (BaseHandler.cs:1263-1293); the simulator confirmed "matched 2 album(s)" but no prompt fired (auto-played). The continuation AlbumIds fix (JF-338) + min-length guard + fuzzy fallback (FindBestMatchWithScore) all remain correct and deployed; only the disambiguation goal is unmet.

Related: JF-336/338 (PlayAlbum fuzzy + tolerant work), JF-339 (PlayAlbum refinements incl. AlbumIds ordering), JF-340 (alexa stop). The disambiguation also applies to EXACT-search multi-match (albums.Count > 1 from the exact Jellyfin query), not just fuzzy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When PlayAlbum resolves multiple albums (exact or fuzzy match) that have DIFFERENT names (e.g. 'Greatest Hits' by Queen vs ABBA), prompt the user to disambiguate (DisambiguationHelper.AskFirstMatch) instead of silently auto-playing the best-scoring one.
- [ ] #2 When the multiple albums share the SAME name (e.g. two 'Jazz Cafe' discs), do NOT prompt (the prompt would be useless) — auto-play the best, as today.
- [ ] #3 The fix: the current HandleFuzzyMiss auto-accepts the best match at score >= GetDefaultThreshold, which is correct for single-match but suppresses disambiguation for multi-match. Either bypass HandleFuzzyMiss for multi-match (go straight to AskFirstMatch) or add a 'disambiguate-if-multiple-distinct-names' mode to HandleFuzzyMiss.
- [ ] #4 Add a unit/integration test: multi-match different-name albums → disambiguation prompt; multi-match same-name albums → auto-play.
- [ ] #5 No regression: single-match albums still auto-play; the JF-336 accent case (jazz caffè → Jazz Cafe) still works.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
