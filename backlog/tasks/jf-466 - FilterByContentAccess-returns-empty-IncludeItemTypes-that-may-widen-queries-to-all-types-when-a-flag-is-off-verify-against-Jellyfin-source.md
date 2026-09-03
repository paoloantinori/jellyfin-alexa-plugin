---
id: JF-466
title: >-
  FilterByContentAccess returns empty IncludeItemTypes that may widen queries to
  all types when a flag is off (verify against Jellyfin source)
status: In Progress
assignee: []
created_date: '2026-09-03 08:38'
updated_date: '2026-09-03 13:05'
labels: []
dependencies: []
references:
  - JF-464 review finding A
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
    (FilterByContentAccess ~1160)
  - CLAUDE.md layered dependency verification rule
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-464 review pass (2026-09-03, finding A, score 85). BaseHandler.FilterByContentAccess (BaseHandler.cs ~1160-1170) returns an EMPTY IncludeItemTypes array when every requested kind is disabled by the media-type flags (e.g. MusicEnabled=false and the query asked for Audio). The suspicion, NOT yet verified in Jellyfin source: the server-side query translator treats an empty (as opposed to null) type array as NO filter, so the query returns items of ANY type. If true, a genre shared between music and movies (e.g. "Action", "Soundtrack") returns non-audio items on a PlayByGenre genre HIT with music disabled, and PlayByGenreIntentHandler queues and plays them through the audio stream URL (wrong playback for a music-disabled library view).

MANDATORY FIRST STEP: verify the empty-array semantics against the ACTUAL Jellyfin 10.11 source (read the query translator / ItemQuery resolution at the tag matching the running server, per the layered-dependency-verification rule: source first, then confirm the running artifact). If the translator treats empty as no-filter, the fix is in FilterByContentAccess: return an explicit empty RESULT signal (or make callers treat empty types as a hard zero) instead of an array that silently widens the query. If the translator treats empty as match-nothing, this task is INVALID: close it with the evidence.

Acceptance criteria:
- Documented verdict on the empty-array semantics with the Jellyfin source line(s) cited.
- If confirmed: FilterByContentAccess no longer produces a silently-widening empty array; regression test where music-disabled + genre shared with movies issues no playback and returns the appropriate not-found.
- All existing FilterByContentAccess callers audited for the same shape (grep every caller, per the N-dispatch-sites rule).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EVIDENCE STRENGTHENED (JF-467 worker, 2026-09-03): two concrete call sites assign FilterByContentAccess's possibly-empty result directly to IncludeItemTypes: PlayByGenreIntentHandler ~:116 and PlayRandomIntentHandler ~:199. SearchMediaIntentHandler's own in-code comment already documents that an empty IncludeItemTypes means 'all kinds' to Jellyfin, so with music disabled those two queries run UNFILTERED by kind and can return non-audio items. The Jellyfin-source verification step is still mandatory (confirm the translator behavior at the running version), but the plugin-side call sites to audit are now identified.
<!-- SECTION:NOTES:END -->
