---
id: JF-533
title: >-
  FindSong no-match cross-media fallback suppressed by an unresolved ArtistName
  (musician given but never resolved, ArtistId null)
status: To Do
assignee: []
created_date: '2026-09-09 21:40'
labels:
  - findsong
  - cross-media-fallback
  - code-review-finding
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:546'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-530 code review (2026-09-09), below the review's reporting threshold; pre-existing, NOT introduced or worsened by JF-530 (verified against the diff).

Gap: in FindSongIntentHandler.SearchAndRespondAsync the no-match cross-media fallback is gated on `string.IsNullOrWhiteSpace(sessionData.ArtistName)`. HandleFirstInvocationAsync stores ArtistName = the raw musician slot input even when the artist does NOT resolve (ArtistId stays null, lines ~220-236). So when the invocation's musician is unresolvable (typo/obscure name) and the user then answers the keywords prompt with a confident ARTIST name, the keywords-as-artist fallback (TryEntityFallbackAsync) is skipped entirely because a stale unresolved ArtistName occupies the gate, and the user gets a plain no-match.

Scenario: "find a song by dj shadoww" (typo, unresolved) -> keywords prompt -> user answers "koop" -> searched only as title keywords -> miss -> no cross-media artist chance for "koop".

Desired outcome: an unresolved artist (ArtistId == null) should not suppress the keywords-as-artist fallback on the no-match path; either gate on ArtistId instead of ArtistName, or clear/flag ArtistName when resolution failed at first invocation. Watch for regressions on the JF-479/JF-463 cross-media gate interactions and the JF-295 multi-word guard (which still applies inside TryEntityFallbackAsync).
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
