---
id: JF-847
title: >-
  VideoApp pause is state-aware: AudioPlayer.Stop stops the video iff context
  PlayerActivity is PLAYING (2026-10-09 live evidence), speak
  VideoStoppedByVoice vs the honest cannot-pause line
status: Done
assignee: []
created_date: '2026-10-09 13:49'
updated_date: '2026-10-09 23:06'
labels:
  - videoapp
  - live-evidence
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Renumbered JF-846 to JF-847 (the fifth same-window number race, resolved per the JF-821 pre-merge precedent on the orchestrator's instruction): main's ledger already carried a different JF-846 (Maintenance-window transparency during catalog syncs, created 2026-10-09 11:54) when this filing was created at 13:49 under a dispatch that pre-dated awareness of it; main's task owns the number.

Live device evidence (2026-10-09, maintainer's tests, twice run): during VideoApp playback "pausa" routes to PauseIntentHandler, which answered the JF-564 honest cannot-pause line plus AudioPlayer.Stop. Once the video STOPPED (the directive reached the active player; the next request's context showed playerActivity STOPPED with the frozen offset); once it did NOT (the context carried playerActivity STOPPED with a STALE token from a previously-stopped launch, while the relaunched video played outside the pipeline the directive reaches). CONCLUSION: AudioPlayer.Stop CAN stop a VideoApp video iff the request context's AudioPlayer.PlayerActivity is PLAYING (the video is the pipeline's active stream); otherwise the directive is inert. This REFINES the 2026-09-07 CLAUDE.md claim "a stop that arrives does NOT stop the video": that observation holds only for the non-playing shape. The fix (implemented in this task's change set): PauseIntentHandler's JF-564 VideoApp-medium arm branches on PlayerActivity: PLAYING speaks the new VideoStoppedByVoice key ("Ho fermato il video." voice, minted in all 17 locales, on the ResponseStringsTests ledger per the JF-821 convention) as a session-ending Tell with the stop directive as today; anything else (STOPPED/IDLE/PAUSED/null, and BUFFER_UNDERRUN deliberately: unobserved for a VideoApp stream, the honest refusal is the safe failure) keeps the honest CannotPauseVideoByVoice line, unchanged. The repo-root CLAUDE.md Stop/Session Routing reference carries the amendment. Residuals recorded for the orchestrator (not skips): (1) the wider JF-564 Cannot* family (CannotNavigateMusicByVoice, CannotNavigateLiveTvByVoice, CannotSetSleepTimerOverVideo, ...) is still outside the AllExpectedKeys ledger; this change added only the two keys its branch speaks. (2) From the /simplify round: the PLAYING-only activity compare now has two private copies (ResumeIntentHandler and this branch); per the repo's extraction-on-convergence convention a NAMED sibling beside PlaybackLaunchBuilder.IsActivelyPlaying (e.g. IsPipelinePlaying) becomes due at the THIRD caller (the SetPlaybackSpeed/SleepTimer refusals are the plausible next consumers if their wording ever goes state-aware, per the altitude review). (3) From the /code-review high round, REJECTED with reasons: a token discriminator (honest line when the context token resolves to an item other than the ledger item), because the 2026-10-09 evidence identifies the ACTIVITY alone as the discriminator and the motivating corner (a live AUDIO-owned PLAYING stream under a VideoApp-classified ledger) is unreachable through skill-initiated playback, as every audio launch re-records the ledger to the Audio route (ResolvePlayingMedium's own doctrine); and per-medium stopped keys for LiveTv/audiobook ("I've stopped the channel/book"), because the JF-564 family already carries the video-noun simplification in its refusal line, the dispatch scoped ONE new key per locale, and live-TV/book PLAYING behavior is unobserved.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 PauseIntentHandler's VideoApp-medium arm branches on context.AudioPlayer?.PlayerActivity: PLAYING speaks VideoStoppedByVoice (session-ending Tell + AudioPlayer.Stop); any other activity keeps CannotPauseVideoByVoice, both with the stop directive
- [x] #2 VideoStoppedByVoice present in all 17 locale files, mirroring each locale's CannotPauseVideoByVoice voice, and carried on the ResponseStringsTests AllExpectedKeys ledger
- [x] #3 Red-green pins in VideoAppGapHonestResponseTests: the PLAYING shape spoke the old line before the fix (red, both TFMs) and the stopped line after; the stale-STOPPED and BUFFER_UNDERRUN shapes keep the honest line
- [x] #4 The repo-root CLAUDE.md Stop/Session Routing section records the 2026-10-09 evidence replacing the blanket claim
- [x] #5 Full suite green both TFMs; validate_locales passes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
COMPLETION RECORD (2026-10-10 01:40, orchestrator - the worker's change merged as a7d98849 + merge 4372f703 but this file's boxes were never ticked; all evidence cited from the commit and re-verified in tonight's tree): AC#1 the PauseIntentHandler PLAYING-branch is in the merged tree (the red-green pins ran BOTH TFMs against the unmodified handler first: the PLAYING pin failed speaking the old CannotPauseVideoByVoice line, green after; counterpart pins hold the honest line on the live stale-STOPPED shape and on BUFFER_UNDERRUN; the pause+cancel theory pair came from the review). AC#2 VideoStoppedByVoice minted in all 17 locale files + both it and CannotPauseVideoByVoice added to the AllExpectedKeys ledger (residual #1, the wider Cannot* family, is NOW JF-849). AC#4 the CLAUDE.md Stop/Session Routing reference carries the 2026-10-09 observed-states amendment. AC#5 suite 5585/5585 both TFMs at the merge; the night's final-tree gate re-proved 5608/5608. DoD 4-8 N/A per surface (no session DTOs, no HttpClient, no fixtures, no E2E beyond the AC#3 pins, locale strings ARE the AC#2 deliverable); DoD 9/10 ran in the worker session (simplify 4 agents: 2 applied 2 skipped with reasons; code-review high: 3 applied, 2 rejected with reasons recorded above) and again at night-batch level in the orchestrator session. Residual #2 (the PLAYING-compare named sibling at the third caller) stays a convention note, no task. Deploys with the next batch.
<!-- SECTION:NOTES:END -->

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
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
