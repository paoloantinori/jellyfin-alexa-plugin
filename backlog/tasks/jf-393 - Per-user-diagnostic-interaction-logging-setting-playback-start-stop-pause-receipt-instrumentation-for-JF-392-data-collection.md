---
id: JF-393
title: >-
  Per-user diagnostic interaction logging setting (playback-start + stop/pause
  receipt instrumentation for JF-392 data collection)
status: In Progress
assignee: []
created_date: '2026-08-22 19:26'
updated_date: '2026-08-22 19:53'
labels:
  - observability
  - playback
  - diagnostics
  - stop
dependencies: []
references:
  - claudedocs/research_alexa_stop_routing_2026-08-22.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a per-user (with global default) "diagnostic interaction logging" setting to the plugin. When enabled for a user, the plugin logs a structured, greppable record for every Alexa interaction, designed to support remote troubleshooting of intermittent routing issues (initial driver: JF-392 'alexa stop' intermittent failure) but useful generally when helping other users.

What to log (one Info-level line per event, keyed by a stable prefix e.g. [diag]):
- Playback start (PlaybackStarted event): device id, session.new of the originating play request if known, timestamp.
- Every AudioPlayer.Play issued: timestamp, token, playBehavior.
- PauseIntent / StopIntent / SessionEndedRequest receipt: timestamp, time since last PlaybackStarted for that device, request type.
- PlaybackStopped: timestamp, reason context.

The key correlation for JF-392: for each stop attempt observed on-device, we can reconstruct (invocation mode at play time, elapsed time since play start, whether the intent reached the skill). Absence of the intent in logs while the user said stop = routing failure instance; user reports the attempt time or we compare with Alexa app history.

Design: reuse the existing per-user override to global default pattern (like GetSearchResponseMode). Gate logging on the flag to avoid log noise for normal users. Follow-up discriminator per JF-392: collect N>20 instances.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-08-22: code complete + committed 7b91214. Remaining: deploy to minix (remember: config.html is an embedded resource - delete output DLL before Release build, verify served page with cache-buster curl), then enable the flag for the test user and collect [diag] data for JF-392.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, reviewed, and committed (7b91214). Per-user DiagnosticInteractionLogging (tri-state) + global DefaultDiagnosticInteractionLogging (false); [diag] lines at controller choke point (requestId, sincePlaybackStarted) and PlaybackStarted/Stopped; config UI in new Diagnostics section + user panel; PATCH endpoints whitelisted. Build 0 warn/err, 2677/2677 tests. /simplify and /code-review high both run; review caught 2 real config-page bugs (dilSel ReferenceError aborting user saves; global flag missing from generalConfig PATCH), both fixed pre-commit. Deploy to minix + live data collection still pending (JF-392 discriminator needs N>20 instances).
<!-- SECTION:FINAL_SUMMARY:END -->
