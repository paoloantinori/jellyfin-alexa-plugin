---
id: JF-294
title: Make SendProgressiveResponse fire-and-forget to remove 50-200ms handler block
status: Done
assignee: []
created_date: '2026-06-16 08:33'
updated_date: '2026-06-16 09:49'
labels:
  - performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlaySongIntentHandler.cs:143 (and other handlers) call `await SendProgressiveResponse(...)`, a best-effort "searching…" ping to the Alexa API. This blocks the handler 50-200ms on every play — the single largest controllable chunk of the observed 247-675ms handler times (2026-06-16 morning logs). ArtistSearch itself was 0-6ms, so this awaited call is a meaningful share of wall-clock time.

The progressive response is non-critical: if it fails or is slow, the real response still arrives within Alexa's 8s timeout. Goal: invoke it fire-and-forget (do not await) so it never blocks the handler response. Audit ALL callers of SendProgressiveResponse (defined in Alexa/Handler/BaseHandler.cs:975), not just PlaySong.

Must ensure exceptions in the fire-and-forget task are swallowed/logged and never propagate to the response pipeline.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SendProgressiveResponse is invoked fire-and-forget (not awaited) across all callers, so it does not block the handler response
- [ ] #2 Exceptions inside the progressive-response task are logged/swallowed and never affect the main SkillResponse
- [ ] #3 Handler 'Completed IntentRequest in Xms' times no longer include the progressive-response round-trip
- [ ] #4 Existing progressive-response unit tests updated; a new test asserts the handler returns without awaiting the ping
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SendProgressiveResponse is now fire-and-forget across all 24 intent handlers via a new RunFireAndForget helper (observes the task via OnCompleted+IsFaulted, CA2012-clean under -warnaserror). SendProgressiveResponse is fully self-protecting (try/catch, never faults). ContinueWatchingIntentHandler converted from async to non-async Task.FromResult (its only await was the ping). Tests: progressive response never faults on internal throw; RunFireAndForget observes a faulted task; handler returns without awaiting. Build green (0 warnings), 2400 tests pass. Committed a8da8dc.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
