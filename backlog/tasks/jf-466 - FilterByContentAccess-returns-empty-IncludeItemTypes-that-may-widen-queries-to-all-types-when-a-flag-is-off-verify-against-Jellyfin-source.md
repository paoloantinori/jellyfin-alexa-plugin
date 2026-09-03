---
id: JF-466
title: >-
  FilterByContentAccess returns empty IncludeItemTypes that may widen queries to
  all types when a flag is off (verify against Jellyfin source)
status: Done
assignee: []
created_date: '2026-09-03 08:38'
updated_date: '2026-09-03 14:03'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
VERDICT: the bug is REAL, fixed, deployed, and live-verified (commits 20c43d14 + the it-IT stream b3dbc952 rides the same DLL).

Verification (source at tag v10.11.11, the v-prefix matters, commit 1fbd873): BaseItemRepository.cs:1799-1829 applies NO type filter when IncludeItemTypes.Length == 0 (only the exclude filter); IsTypeInQuery (:1548) `query.IncludeItemTypes.Length == 0 || Contains(type)`; the CommaDelimitedCollectionModelBinder converges null/omitted/empty-param on the same length-0 array. Empirical probe on the live server: genre Action = 275 items with an empty type param, 0 with Audio. With music disabled, PlayByGenre on a movie-shared genre would have queued 275 non-audio items through the audio stream URL.

Fix: a CONTRACT doc on FilterByContentAccess (empty means all-kinds-disabled; callers hard-zero or gate, never assign to IncludeItemTypes) plus the full 13-caller audit: PlayByGenre entry music gate (JF-467 convention; CLAUDE.md bullet updated), PlayRandom ResolvePlayableKinds hard zero, Recommend and both BrowseLibrary paths hard zeros, the shared FindLastPlayedItemWithProgress choke point (null,0) covering Resume/ContinueWatching/StartOver, SearchMedia structurally non-empty (Playlist never filtered, pinned by an existing test). Disabled hard zeros speak the shared localized MediaTypeNotAvailable (review finding applied: the empty-library copy would be factually wrong). Review follow-ups folded: PlayRandom and Recommend gates moved before their searching announcements; the BrowseLibrary reordering documented as a deliberate leave; one stale test name renamed.

Live verification on minix: PATCH MusicEnabled=false + simulator genre "Action" (the exact 275-item probe genre) returns "This type of content is not available." with no AudioPlayer directive; flag restored, config verified (MusicEnabled=true, 1 user).

Suite 3085/3085 (9 new tests, each mutation-verified), Release 0 warnings, validators baseline, NLU dry-run unchanged. Known corners documented: ContinueWatching's kind set lacks AudioBook (a books-only user mid-audiobook now hears NoContinueWatching instead of the accidentally-widened resume; the old path was the bug); the contract is doc-only (compiler cannot prevent a future empty assignment; escalation path is a TryFilterByContentAccess shape if it recurs).

Gates: /simplify + code-review combined in one pr-review-toolkit:code-reviewer pass (zero findings >= 80; the announcement-before-gate wart scored 75 and the trivial halves were applied anyway; the stale test name 65 applied).
<!-- SECTION:FINAL_SUMMARY:END -->
