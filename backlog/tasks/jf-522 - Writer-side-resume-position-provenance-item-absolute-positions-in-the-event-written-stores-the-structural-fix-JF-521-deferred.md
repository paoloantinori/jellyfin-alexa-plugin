---
id: JF-522
title: >-
  Writer-side resume-position provenance: item-absolute positions in the
  event-written stores (the structural fix JF-521 deferred)
status: To Do
assignee: []
created_date: '2026-09-08 00:36'
labels:
  - resume
  - transcoding
  - tech-debt
  - follow-up
dependencies: []
references:
  - JF-521
  - JF-520
  - JF-514
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The deeper fix for the resume-offset provenance family, deliberately rejected by JF-521 (recorded there) and now tracked per the every-recommendation-lands rule. The JF-521 audit found gaps only a WRITER-side fix closes: the event handlers (PlaybackStoppedEventHandler, PlaybackStartedEventHandler) persist RAW device offsets into server UserData, session PlayState, ItemPositionState, and DeviceQueue.CurrentPositionTicks, so every non-Echo consumer of those stores (other Jellyfin clients reading UserData, MediaInfo position display, SkipForwardBack/GoToChapter position math for transcode-routed items) sees stream-relative values presented as positions. JF-521's reader-side gate protects only the skill's own resume offer; the stores themselves stay wrong.

This is the long-term structural fix; JF-521's equality gate remains correct regardless (it composes with either writer regime). Rejected at JF-521 time for three reasons (all recorded there): every compensating reader must flip atomically, the writer's base-at-stop-time precondition is unsafe against the wrapped-queue ledger clobber, and errors would persist server-side across deploys. Tackle only with the audit budget to do it atomically.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Audit re-run at implementation time: enumerate every reader that compensates for stream-relative persisted positions (ResumeIntent fallbacks 2-3, SkipForwardBack, GoToChapter, MediaInfo/BuildPositionDisplay, AplUserEventHandler.GetResumeOffset, FindResumeTrackIndex, every UserData reader incl. other Jellyfin clients) and flip them in the same change
- [ ] #2 Solve the writer-precondition hazard the JF-521 rejection documented: the ledger is a last-RESOLVE ledger clobbered mid-playback on wrapped/repeat-one queues (PlaybackNearlyFinished/PlaybackStarted precompute), so a naive writer persists wrong 'item-absolute' values; the fix needs base capture at launch time carried to the stop event (e.g. via the queue state or a launch-scoped record)
- [ ] #3 Rolling-deploy story: server-persistent UserData has no provenance flag; the migration/compat behavior for mixed old/new positions must be defined and tested
- [ ] #4 The JF-521 equality gate and the OffsetIsStreamRelative flag become removable only if ALL sources turn item-absolute (incl. Amazon's AudioPlayer context offset, which is stream-relative by platform contract and CANNOT be fixed - so the flag likely survives; state the endgame explicitly)
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
