---
id: JF-906
title: >-
  Loop speaks repeat-one but the queue advances: the repeat mode write is wiped
  (stop-report PlayState overwrite suspected) and the per-device queue's
  SetRepeatMode has no production caller
status: Done
assignee: []
created_date: '2026-10-18 08:45'
updated_date: '2026-10-18 10:44'
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
EXECUTED on branch jf906 (540a571e), merged to main 88806c3e, pushed, DEPLOYED to minix (md5 4a7dc258 active, config survived, probes green: play pipeline healthy, LoopOn speaks the repeat-one line). ROOT CAUSE (verified in Jellyfin SOURCE at tags v10.11.8/v12.0/v12.1, the check-the-real-artifact rule): SessionManager.UpdateNowPlayingItem sets PlayState.RepeatMode from EVERY playback report, and OnPlaybackStart arms StartAutomaticProgress, a 1-SECOND timer re-firing the launch-time snapshot - so the JF-280 in-memory write never survived ~1s; the device's 4-min window was irrelevant. The suspected stop-report vector is real but secondary (the displacement arm's RestoreCurrentStartAsync re-applies the same snapshot). THE FIX: the per-device queue is the ONE durable owner (ApplyRepeatModeAsync -> SetRepeatMode with coherence seeding: empty store seeds the current item, foreign queue front-inserts preserving members); ONE queue-first resolver with a coherence gate (a foreign queue answers RepeatNone, never the stale session copy) feeds the NearlyFinished precompute gate, ResolveNextItemId, the Finished keep-alive arms, the playback-start report belt, and ReportPlaybackProgress; the session write stays as non-authoritative dashboard display. The code-review round empirically probe-verified TWO leak classes in the first cut (a stale looped queue looping every later single-song play; the write planting a durable empty mode-only queue) and both were closed by the gate + seeding, with 4 leak-matrix pins; confirmation APPROVED. Gates (in 540a571e): 0-warning build both TFMs Debug+Release -warnaserror, 6050/6050 per TFM (+9 pins), /simplify pass, code-review high 6 applied + APPROVED, validators PASS. Red proof 8/9 stash-verified (the ninth proven red by the reviewer's probe on the ungated intermediate). RESIDUALS documented: progressive-continuation pages are session-queue-only (loop on page 1 stops governing at the first continuation boundary; KNOWN LIMIT on the resolver); ResolvePlaybackOrder has the same foreign-queue exposure without a gate (pre-existing JF-447 design, harmless today); the foreign-queue front-insert mutates a dead queue's membership (equals the pre-existing order exposure, accepted). DEVICE RETEST PENDING (the release-gate item): 'attiva il loop' then track end must re-enqueue the SAME item (log check on the NearlyFinished line: loop=One source=deviceQueue). gate-exempt: gates ran in the dispatched worker's transcript; commit 540a571e carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
