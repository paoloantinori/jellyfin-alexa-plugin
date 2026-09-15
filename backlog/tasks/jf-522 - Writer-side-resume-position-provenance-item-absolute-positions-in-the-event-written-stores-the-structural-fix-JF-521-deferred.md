---
id: JF-522
title: >-
  Writer-side resume-position provenance: item-absolute positions in the
  event-written stores (the structural fix JF-521 deferred)
status: Done
assignee: []
created_date: '2026-09-08 00:36'
updated_date: '2026-09-15 02:00'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
METHOD DISCIPLINE (2026-09-11, from the TDD question): characterization-first, not classic TDD (the audit may not change observable behavior). Phase 1 = read-only provenance mapping + characterization tests pinning the CURRENT writer/reader provenance chain at every store (green on arrival, they are the regression net for the migration); Phase 2 = the writer-side migration in small steps, suite green with zero expectation edits at each; red-green only for new invariants (e.g. 'position written is item-absolute at every store' as an explicit pin where it newly holds).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED complete (2026-09-15 night, implemented by delegated agent under characterization-first discipline, orchestrator-verified). The writer-side migration shipped atomically: every event-written store (session PlayState, DeviceQueue.CurrentPositionTicks and ItemPositionState, Jellyfin UserData) now receives ITEM-ABSOLUTE positions composed from a launch-scoped base. Phase 1 delivered the provenance characterization suite (raw provenance + ledger clobber pinned, green on arrival); phase 2 shipped in three green steps (additive scopes, the designed flip of the three base-capable callers + active-scope read + unconditional item-absolute seed, then DELETION of the JF-514 last-resolve ledger and its wrappers). The JF-521 rejection hazards are each solved and pinned: clobber by the pending/active split with promote-at-PlaybackStarted; rolling-deploy by conservative-read-then-self-heal with rollback safety; endgame stated - OffsetIsStreamRelative SURVIVES narrowed to Amazon's context offset (platform contract, unfixable), the equality gate retired (would double-add post-fix). Review folded 3 real fixes (Echo device id for shuffle progress, cross-item recovery-pointer reset, launch-scope locking) and filed 2 outside-diff drafts (APL taps under AlexaDevice; mixed-provenance raw-static skips - both in backlog/drafts/). Net +14 tests; suite 3784/3784 both TFMs; Release -warnaserror clean. Gates: implementer-run 4-agent simplify + 5-agent code-review high, orchestrator-verified and formally recorded.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
