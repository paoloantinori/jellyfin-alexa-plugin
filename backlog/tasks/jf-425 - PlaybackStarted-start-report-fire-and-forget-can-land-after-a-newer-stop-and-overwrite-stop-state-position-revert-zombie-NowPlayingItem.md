---
id: JF-425
title: >-
  PlaybackStarted start-report fire-and-forget can land after a newer stop and
  overwrite stop state (position revert, zombie NowPlayingItem)
status: Done
assignee:
  - zai
created_date: '2026-08-31 15:02'
updated_date: '2026-09-02 00:35'
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
- [x] #1 First verify the race empirically (log-based or reasoning from SessionManager): does a late start report actually overwrite a newer stop state? Findings recorded in the task
- [x] #2 If confirmed: the start report can no longer overwrite a newer stop (await, sequence per session, or version/epoch guard in SessionManager)
- [x] #3 If refuted: the code comment stating the ordering guarantee is corrected and the task closed with the evidence
- [x] #4 PlaybackStopped's awaited report and the 8s-window budget both remain intact (no new blocking on the Alexa hot path)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-425: a stalled playback-start report can no longer resurrect a stopped session (zombie Playing state, stale NowPlayingItem); the two confirmed review-round bugs fixed same-night; the residual interleavings filed as JF-447.

WHAT CHANGED (commits a07149c)
- PlaybackReportOrdering (new): the per-device superseding-stop slot. PlaybackStarted clears it before dispatching its fire-and-forget report; Stopped/Finished/Failed set it before their awaited reports; when the start report completes with a stop in the slot it re-issues that stop (the host call that clears the session). Verified against the Jellyfin 10.11.8 host source: the resurrecting write is host-internal (OnPlaybackStart awaits GetMediaSource before writing PlayState), so prevention is impossible and the guard restores the invariant. A numeric epoch was implemented first and collapsed (two /simplify agents + a truth table: identical to the slot FOR STOP CORRECTION). Zero awaits added to the Alexa response path.
- Review round fixes: Finished and Failed gained the displacement guard Stopped already had, via the shared PlaybackReportOrdering.IsDisplacementStop (single definition; Stopped refactored onto it; the guard prevents a displaced old stream's stop from being recorded and replayed against the NEW track). The composite sleep-timer token crash in Failed (new Guid on '{guid}|sleep:{ticks}' threw before RecordStop and the required ack) fixed with a defensive parse.
- Constraints documented: the microseconds-long zombie write before the corrective stop is host-internal; displacement stops and loop-toggle progress writers are deliberately outside the protocol (class doc).

VERIFICATION
- Tests (8 total): the AC interleaving exactly (stalled start -> stop completes -> gate released -> stop re-issued exactly twice), normal ordering (no re-issue), displacement Finish/Failed (no correction), real-finish polarity (correction fires), sleep-timer composite token (completes + records + re-issues), stall resilience, ack shape. Suite 2824 -> 2832/2832; Release 0 warnings (verified by three agents across the night).
- Gates: /simplify (implementer 4 + fix agent 4; epoch collapse, renames, dead usings); code-review high (10 findings: 2 confirmed bugs applied same-night - displacement guard + composite token; 8 filed as JF-447 with its notes: start-vs-start, correction re-validation, queue-state trust, double-stop, plus the codec/RecordStop consolidation and the three sibling new Guid sites).
- Rides the next deploy; live check = playback sessions after rapid stop-then-play transitions (the zombie card was the observable).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining
- [x] #11 or findings applied/tracked)
<!-- DOD:END -->
