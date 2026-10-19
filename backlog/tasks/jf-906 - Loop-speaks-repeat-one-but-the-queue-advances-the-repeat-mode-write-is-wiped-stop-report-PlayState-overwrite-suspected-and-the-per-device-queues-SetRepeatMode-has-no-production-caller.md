---
id: JF-906
title: >-
  Loop speaks repeat-one but the queue advances: the repeat mode write is wiped
  (stop-report PlayState overwrite suspected) and the per-device queue's
  SetRepeatMode has no production caller
status: Done
assignee: []
created_date: '2026-10-18 08:45'
updated_date: '2026-10-19 12:44'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DEVICE-VERIFIED BUG 2026-10-18 ~10:42 (Paolo's release-gate round, minix logs): 'attiva il loop' answered 'Ripetizione del brano attivata.' (corr 477ed915, RepeatOne written) yet at track end the queue ADVANCED to the next track. LOG CHAIN: 10:38:32 queue created repeat=None; 10:38:37 PlaybackStarted precomputed next='Salirò' (before the loop); 10:38:45 LoopOn -> ApplyRepeatModeAsync(RepeatOne) sets session.PlayState.RepeatMode directly (the JF-280 in-memory write) + invalidates the precompute (verified: the NearlyFinished fell through to full resolution, so invalidation WORKED); 10:42:25 PlaybackNearlyFinished resolved next=Salirò with loop=RepeatNone read from session.PlayState (PlaybackNearlyFinishedEventHandler.cs:199) - the direct write was GONE by then. Suspected wipe: the track-1 PlaybackStopped stop-report (ReportStopOrderedAsync fires OnPlaybackProgress with a PlaybackProgressInfo whose RepeatMode defaults to RepeatNone) replaces session.PlayState server-side, wiping the in-memory write in the window between the loop command and track end. SECOND, structural: DeviceQueueManager.SetRepeatMode (line 1945) has NO production caller - the per-device queue (the durable truth Advance reads at line ~1872: queue.RepeatMode 'One' stays, 'All' wraps) is NEVER written by the loop family, so even absent the wipe the queue mode stays None. FIX DIRECTION: make the per-device queue the ONE durable owner: ApplyRepeatModeAsync calls queueManager.SetRepeatMode(deviceId, One/All) (LoopOff clears to None), the NearlyFinished resolution/ResolveNextItemId consults the queue mode (Advance already does), and the stop-report no longer needs RepeatMode semantics on the session (keep the session write for dashboard only, documented as non-authoritative). Also check PlaybackStopped/Finished stop reports for the PlayState-wipe mechanism and pin it. PINS: RepeatOne via AMAZON.LoopOn -> track end re-enqueues the SAME item id (the JF-901 device matrix); RepeatAll wraps at queue end; LoopOff clears; the wipe-window pin (loop set, stop event, then NearlyFinished reads the queue mode still One). VERIFY ON DEVICE post-deploy: the maintainer's standing retest.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DEVICE-VERIFIED 2026-10-19 14:38-14:42 (Paolo's round): 'attiva il loop' during playback → 'Ripetizione del brano attivata.' (corr a70f883d, LoopOnIntent at 14:38:57) → at track end: PlaybackNearlyFinished (14:41:46) → PlaybackFinished (14:41:58) → PlaybackStarted of the SAME item (14:41:58, sincePlaybackStarted=245.6s) = the track REPEATED. The JF-906 queue-owned repeat mode works on hardware; the release blocker is closed. Also verified: the whole flow completes inside the Alexa window (no INVALID_RESPONSE). gate-exempt: the fix's gates ran in the worker transcript (540a571e) and the device round is the acceptance.
<!-- SECTION:FINAL_SUMMARY:END -->
