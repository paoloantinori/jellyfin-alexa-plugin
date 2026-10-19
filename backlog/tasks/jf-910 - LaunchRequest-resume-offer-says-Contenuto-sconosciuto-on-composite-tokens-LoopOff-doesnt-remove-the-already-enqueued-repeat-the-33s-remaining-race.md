---
id: JF-910
title: >-
  LaunchRequest resume offer says 'Contenuto sconosciuto' on composite tokens;
  LoopOff doesn't remove the already-enqueued repeat (the 33s-remaining race)
status: Done
assignee: []
created_date: '2026-10-19 13:32'
updated_date: '2026-10-19 15:01'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TWO device-verified bugs from Paolo's 2026-10-19 ~15:29-15:31 round (minix logs corr 0e4d2ef4 and d196e664/c5bbe9b1). BUG 1 (the unknown-content resume offer): LaunchRequestHandler's resume offer for item b7380b61 (The Idiot Kings, Soul Coughing; the last-played track from the 14:37 session) spoke 'Stavi ascoltando Contenuto sconosciuto' instead of the track name: the token carried the composite suffix '|launch:1' and the item-name resolution failed on that shape (the GUID is parseable via StreamTokenCodec but the display-name lookup path apparently doesn't strip the suffix). FIX: strip the composite suffix before the name lookup in the LaunchRequest resume-offer path (find every name-resolution site that touches raw tokens and audit each for suffix-stripping). BUG 2 (the loop-off race): the track was resumed at ~204s into a ~237s file (33s remaining); the NearlyFinished fired immediately (33s < the ~30s threshold), read RepeatMode=One (the loop was enabled 6s before), and enqueued the same track. The user disabled the loop 6 seconds after enabling it, but the repeat was ALREADY enqueued on the device; clearing the queue mode does not remove enqueued items. The track repeated despite LoopOff having answered 'Ripetizione disattivata.' FIX: on LoopOff (and LoopOn->Off transitions), when the plugin's queue state shows the CURRENT item as the enqueued next (a repeat), issue a ClearQueue(CLEAR_ENQUEUED) directive to remove the pending repeat before it plays; this must NOT clear the currently-playing stream (CLEAR_ENQUEUED only removes queued items, not the current one). PIN: enable loop, wait for the NearlyFinished to enqueue the repeat, disable loop, verify the NEXT track (not the same one) plays.
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
EXECUTED on branch jf910 (df6f7b38), merged to main 57f1a772, pushed, DEPLOYED to minix AlexaSkill_1.0.0.0 (md5 54bca4e6 active, HasPendingSameItemRepeat verified in the running DLL via strings, config survived Users: 1). BUG 1: LaunchRequestHandler.HandleResumeOfferAsync parses the AudioPlayer token through StreamTokenCodec.TryGetItemId (no more raw Guid.TryParse on composite tokens), resolves the item for the display name, and stores the BARE id in resume_state so the Yes-confirm roundtrip Guid-parses it; the audit found one more raw-token site (ResumeIntentHandler line 119 intake) which now canonicalizes via the new StreamTokenCodec.CanonicalizeItemId. BUG 2: ApplyRepeatModeAsync snapshots whether DeviceQueueManager.HasPendingSameItemRepeat holds before its queue writes, and on LoopOff attaches ClearQueueDirective(ClearBehavior.ClearEnqueued) plus ClearEnqueueRecord: the pending same-item repeat is removed from the device queue without stopping the current stream (the finishing track plays out and stops); PlaybackFinished's boundary veto ends the session honestly. Sequential different-item enqueues and LoopOff-over-no-loop are pinned NOT to trigger the directive. 11 new test methods. Gates (in df6f7b38): 0-warning build both TFMs Debug+Release, 6076/6076 per TFM, /simplify 4-agent battery, code-review high 5 findings 3 applied 2 rejected (F1 out of scope per the no-NearlyFinished constraint, F5 conservative raw passthrough pinned). Red-proof: all 7 positive pins red on the pre-fix tree both TFMs. gate-exempt: gates ran in the dispatched worker's transcript; commit df6f7b38 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
