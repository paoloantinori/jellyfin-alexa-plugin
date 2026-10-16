---
id: JF-898
title: >-
  Rejected-climb fresh launches of Audio-typed remapped chapters still classify
  VideoAppAudio mid-play: the launch-verdict mint covers only accepted-climb
  arms
status: Done
assignee: []
created_date: '2026-10-16 10:39'
updated_date: '2026-10-16 14:22'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual from the JF-894 round (worker handback, 2026-10-16): the REJECTED-climb fresh-launch shape of an Audio-typed remapped chapter (shared container of sibling books / dangling ParentId, the JF-793 rejection cases) still records bare LaunchRoute VideoApp in the ledger, so mid-play it classifies VideoAppAudio and voice pause speaks the music line over a book. The JF-894 fix covers only the ACCEPTED-climb arms (the launch sites record VideoAppAudiobook when the verified climb accepted). Out of that task's scope by design; same family as the JF-799 residuals. FIX SHAPE when taken: the rejected-climb launch path records nothing book-like today; either record a distinct route value at the rejected arm (the launch KNOWS the caller intended a book even when the climb failed, the caller-intent doctrine) or accept and document the corner as unreachable for real libraries (the census shape's collapsed single-file books). Decide with the JF-799 residual family in one pass.
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
MAINTAINER DECISION 2026-10-16 ~15:3x: option (a), record the book-intent route at the rejected-climb arm. Dispatched to the jf898 worker lane (worktree /tmp/wt-jf898). Device retests for the other rows deferred: the maintainer is out of the house.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf898 (79557aa3), merged to main 2f57ac1f, pushed, DEPLOYED to minix via the LAN IP (the .lan resolver dropped mid-session; 192.168.20.201 verified by hostname), active-DLL md5-verified, config survived (1 user), probes green (play pipeline, jones ask byte-identical). THE CHANGE (maintainer decision option a, 2026-10-16): BuildVideoAppAudioResponse's single-item else arm (the rejected-climb fresh-launch serve, behind the isAudioBook || bookChapterLaunch discriminator) now mints LaunchRoute.VideoAppAudiobook via the singleItemBookServe = typeAgnosticClimb flag when bookFolder is null but the arm serves a book intent; the ALBUM-CONCAT arm keeps bare VideoApp (the flag excludes it; pinned by the code-review round's bare-route pin, the AudioBook-empty-ParentId corner). ClassifyLedgerItemKind untouched (the mint flows through the JF-894 route arm). The rejected arm's tracker-clear and flat-resume degradation untouched (by-name runs of the three JF-794/JF-798 pins passed; BuildAudiobookResumeResponse unchanged). Red proof both TFMs (the mint pin: VideoApp vs VideoAppAudiobook; the flow pin: music line over the book). Gates (in 79557aa3): 0-warning build both TFMs, 6036/6036 both TFMs, /simplify 4 agents (fixture hoist + stale doc line applied), code-review high 3 findings all applied. CLAUDE.md audiobook Resume section gained the JF-898 sentence (precision-fixed per review: the mint is gated on the discriminator, not the arm). Residuals: pre-upgrade entries self-heal on next launch (accepted no-migration stance); the RESUME path's rejected arm still records route Audio through the flat chokepoint (out of the fresh-launch decision's scope, noted). gate-exempt: gates ran in the dispatched worker's transcript; commit 79557aa3 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
