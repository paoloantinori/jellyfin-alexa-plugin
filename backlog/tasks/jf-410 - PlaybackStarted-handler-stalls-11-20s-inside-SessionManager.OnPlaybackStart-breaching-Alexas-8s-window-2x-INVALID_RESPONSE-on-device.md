---
id: JF-410
title: >-
  PlaybackStarted handler stalls 11-20s inside SessionManager.OnPlaybackStart,
  breaching Alexa's 8s window (2x INVALID_RESPONSE on-device)
status: In Progress
assignee:
  - zai
created_date: '2026-08-28 15:37'
updated_date: '2026-08-28 16:46'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-28 (two occurrences, 16:23:44 and 16:33:13): AudioPlayer.PlaybackStarted handler took 11339.8ms and 20612.7ms to respond (logged by LoggingResponseInterceptor/MetricsResponseInterceptor "had no body after Nms"). Alexa's ~8s window expired, Amazon reported System.ExceptionEncountered INVALID_RESPONSE ("An exception occurred while dispatching the request to the skill", cause.requestId = the two PlaybackStarted requests amzn1...736d64aa / amzn1...fe6dc72f), and the device spoke "Qualcosa è andato storto". Audio kept playing (enqueue had already happened in NearlyFinished).

Verified so far (do not redo): the entire stall sits inside the single await on SessionManager.OnPlaybackStart (PlaybackStartedEventHandler.cs:81; only await between the entry debug log and the [diag] log). Against Jellyfin v10.11.11 source: IEventConsumer<PlaybackStartEventArgs> has only PlaybackStartLogger in core (fast); Trakt subscribes to the classic queued PlaybackStart event and ignores Audio items; GetMediaSource for local audio is in-memory. Remaining unverified suspects: the synchronous EF/SQLite transaction in UserDataManager.SaveUserData (library scan activity was present that afternoon) or threadpool continuation delay. Only 2 occurrences since container start 04:10, not chronic.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Elapsed-time logging around SessionManager.OnPlaybackStart in PlaybackStartedEventHandler (debug level, always-on) sufficient to localize future stalls to the call
- [x] #2 A decision, recorded in the task notes, on whether the Alexa response should stop waiting on OnPlaybackStart (e.g. fire-and-forget with error logging) or keep current semantics; whatever is chosen, the Alexa 8s budget cannot be breached by this server-reporting side effect
- [x] #3 RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget and the full suite stay green
- [x] #4 No regression in session/progress reporting: PlaybackFinished resume data and Jellyfin playback position still recorded (verify via simulator or unit tests)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. TDD: unit test that PlaybackStartedEventHandler returns promptly (keep-alive ack) even when ISessionManager.OnPlaybackStart never completes (mock returns a never-completing Task; assert completion; verify the call still happens once released).
2. Move the OnPlaybackStart await off the response critical path: run it as a tracked background task (Stopwatch timing, LogDebug with elapsed ms, LogWarning when > 2s so future incidents surface at default log level, catch-all error logging). Response, [diag], and precompute no longer wait on it.
3. Record the decision + rationale in task notes; full dotnet test.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DECISION (AC #2): the Alexa response no longer waits on SessionManager.OnPlaybackStart. Rationale: nothing in the keep-alive ack depends on the result; the live incident proves the call can take 20s inside Jellyfin core (sub-mechanism still undetermined: SQLite SaveUserData contention vs threadpool delay); position data is re-reported by the next playback event even if this one fails.

Implementation: fire-and-forget ReportPlaybackStartAsync with Stopwatch timing (LogDebug always, LogWarning above SlowReportMs=2000 so future stalls surface at default log level), catch-all LogError. HandleAsync became fully synchronous (Task.FromResult) since no awaits remain.

Tests: PlaybackStarted_Handle_ServerReportStalls_StillResponds (gated never-completing OnPlaybackStart; handler must respond within 5s and still call exactly once) failed pre-fix, green post-fix. Existing OnPlaybackStart Times.Once verification still passes (synchronous mock completion, no race). Full suite 2728 passed.

REVIEW VERIFICATION: the fire-and-forget only decouples the response if OnPlaybackStart yields before its slow section - verified against Jellyfin v10.11.11 source that the heavy work (UpdateNowPlayingItem's GetMediaSource await, PublishAsync) sits behind real awaits; the synchronous prefix is in-memory lookups. The SlowReportMs warning is the on-device confirmation instrument: if a future stall still breaches the window, it will show as a slow-report warning WITHOUT a slow response, proving the sync prefix; a slow response WITH the warning would prove otherwise.

FOLLOW-UP RECORDED (code-review finding, not fixed here - outside diff scope): PlaybackNearlyFinishedEventHandler.ResolveNextItemId prefers session.FullNowPlayingItem over context.AudioPlayer.Token, the REVERSE of the documented resume rule; with the report now off-path, a delayed start-report for track N can complete while N+1 plays, making NearlyFinished(N+1) read stale FullNowPlayingItem=N and re-enqueue the current track (the JF-409 symptom via session state). Candidate fix: flip the preference to AudioPlayer.Token first. Track as its own small task if observed on-device.

Test fixed per review: the stall test awaited handleTask instead of blocking .Result (xUnit1031 under CI -warnaserror, reproduced by reviewer, build now clean).
<!-- SECTION:NOTES:END -->

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
