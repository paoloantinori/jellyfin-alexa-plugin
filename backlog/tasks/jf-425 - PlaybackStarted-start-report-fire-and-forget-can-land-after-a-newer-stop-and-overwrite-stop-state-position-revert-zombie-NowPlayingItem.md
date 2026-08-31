---
id: JF-425
title: >-
  PlaybackStarted start-report fire-and-forget can land after a newer stop and
  overwrite stop state (position revert, zombie NowPlayingItem)
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
labels:
  - code-review
  - playback
  - needs-verification
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackStartedEventHandler.cs:95
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackStoppedEventHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-08-31, high effort, PLAUSIBLE: needs verification before fixing). PlaybackStartedEventHandler.cs:95.

DEFECT SHAPE: fire-and-forgetting the playback-start report drops the old ordering guarantee (start report lands on the server before later events are processed) while PlaybackStopped's report is still awaited. Scenario: user plays a track then stops within seconds (or the track auto-advances); PlaybackStoppedEventHandler awaits OnPlaybackStopped (records stop position, clears NowPlayingItem); the still-in-flight start report, whose own code documents 11.3s/20.6s stalls inside Jellyfin, lands AFTER and overwrites the session: position reverts to the start offset, NowPlayingItem is resurrected; the dashboard shows an active item and server-side resume state is stale until a later event re-reports.

VERIFY FIRST: this is marked PLAUSIBLE because the trigger sequence rests on Amazon event timing + Jellyfin stall evidence in the diff. Confirm by logs or by reading SessionManager's per-session serialization rules before choosing the fix (a full await might reintroduce the JF-410 hot-path stall the fire-and-forget was created to avoid; an epoch/sequence guard in SessionManager may be the right altitude).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 First verify the race empirically (log-based or reasoning from SessionManager): does a late start report actually overwrite a newer stop state? Findings recorded in the task
- [ ] #2 If confirmed: the start report can no longer overwrite a newer stop (await, sequence per session, or version/epoch guard in SessionManager)
- [ ] #3 If refuted: the code comment stating the ordering guarantee is corrected and the task closed with the evidence
- [ ] #4 PlaybackStopped's awaited report and the 8s-window budget both remain intact (no new blocking on the Alexa hot path)
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
