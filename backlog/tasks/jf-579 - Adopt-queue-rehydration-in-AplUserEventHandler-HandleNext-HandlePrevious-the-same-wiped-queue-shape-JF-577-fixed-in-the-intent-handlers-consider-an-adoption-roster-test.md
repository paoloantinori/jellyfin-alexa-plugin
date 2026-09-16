---
id: JF-579
title: >-
  Adopt queue rehydration in AplUserEventHandler HandleNext/HandlePrevious (the
  same wiped-queue shape JF-577 fixed in the intent handlers) + consider an
  adoption-roster test
status: Done
assignee: []
created_date: '2026-09-16 14:17'
updated_date: '2026-09-16 21:25'
labels:
  - reliability
  - queue
  - coverage
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-577 /simplify altitude pass (2026-09-16): AplUserEventHandler.HandleNext (~lines 116-138) and HandlePrevious (~lines 143-166) are line-for-line the same queue-scan shape the JF-577 adoption fixed in Next/PreviousIntentHandler, including the `Count == 0 || FullNowPlayingItem == null` bail, and they did NOT adopt the ProgressReporter.TryRehydrateSessionQueueFromDevice guard. After a mid-playback restart, an APL NowPlaying-screen next/previous tap would speak/answer from the wiped (empty) session queue while music plays. QueueRehydrationAdoptionTests pins only the three intent adopters and is behavioral, so a forgotten consumer fails silently. Scope when picked: read whether the APL tap request carries context.AudioPlayer (the tap fires while a stream plays, so it should), add the guard + ResolveCurrentItemId adoption in both APL branches, red-first tests in QueueRehydrationAdoptionTests style; if the APL context genuinely lacks the playing token, document why and close. Also consider a coverage-roster test (the WarmingGateCoverageTests IL-scan precedent) listing every session.NowPlayingQueue reader and its adoption status, so the next consumer cannot be forgotten silently. Related: JF-574, JF-577, JF-578.
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
Session 2026-09-16 (implementation; status left In Progress, tree left uncommitted for the orchestrator's gates).

## APL context verdict: ADOPT (the tap carries context.AudioPlayer)

The task's precondition verified first. Amazon's Request and Response JSON Reference (scraped live 2026-09-16, context-object table): `context.AudioPlayer` "is included on all customer-initiated requests (such as requests made by voice or using a remote control), but includes the details about the playback (`token` and `offsetInMilliseconds`) only when sent to a skill that was most recently playing audio." An `Alexa.Presentation.APL.UserEvent` from a TouchWrapper press (the APL interface reference: sent "in response to the user's actions on the device") is a customer-initiated request, and the next/prev tap fires on the NowPlaying screen this skill rendered while this skill was the most recently playing audio, so the token is present. Alexa.NET deserializes the envelope `context` into the same `SkillRequest.Context` for every request type, so a sent token reaches the handler. Corroboration in-tree: `LaunchRequestHandler` already reads `context.AudioPlayer.Token` on a customer-initiated request (the live resume offer), and the JF-564 medium classification reads it on IntentRequest + PlaybackControllerRequest. No captured APL UserEvent envelope exists in the minix podman logs (no AplUserEvent lines in the retained window), so the docs line plus the same-family live precedents are the evidence base; note the adoption is fail-safe regardless, because with no token `TryRehydrateSessionQueueFromDevice` returns false and `ResolveCurrentItemId` returns `FullNowPlayingItem.Id` first, i.e. byte-identical today behavior.

## Adoption shape

Both `AplUserEventHandler.HandleNext` and `HandlePrevious`: the JF-577 guard (`ProgressReporter.TryRehydrateSessionQueueFromDevice` with the handler's already-injected `DeviceQueueManager` singleton; no ctor change needed, unlike ListQueue) plus `ProgressReporter.ResolveCurrentItemId`, called before the queue read, mirroring Next/PreviousIntentHandler. There is no medium-refusal gate on this path to sit after (the NowPlaying APL screen is attached only on audio launches; no VideoApp playback carries it), so the guard is simply at the top. The entry bail `Count == 0 || FullNowPlayingItem == null` became `Count == 0 || currentItemId == null` and the loop compares the resolved id; everything else (launch via `Launch.GetStreamUrl` + `BuildAudioPlayerResponse`, the `FullNowPlayingItem` update, the Empty answers) is untouched, so response shapes are exactly what each branch already produced. Deliberate non-change: HandleNext/Previous keep `Launch.GetStreamUrl` (not NextIntent's `ResolveAudioLaunchSource`); aligning that is codec-gate scope (JF-507 family), not rehydration scope.

## Adoption-roster test

New `Jellyfin.Plugin.AlexaSkill.Tests/Handler/SessionQueueReaderRosterTests.cs` (the WarmingGateCoverageTests precedent, plain System.Reflection + IL bytes, no new dependency). The scan resolves every call/callvirt token in the plugin assembly's method bodies through `Module.ResolveMethod` and collects the top-level declaring types (nested async state machines and closures attributed up) that read the `SessionInfo.NowPlayingQueue` getter; the roster is asserted in BOTH directions, and a second test mechanically proves the ADOPTED label by scanning raw methoddef tokens for calls to the guard. Roster as scanned (13 types exactly): ADOPTED = NextIntentHandler, PreviousIntentHandler, ListQueueIntentHandler, PlaybackNearlyFinishedEventHandler, AplUserEventHandler (this task); EXEMPT with reasons (one line each, in the roster comment) = PlaybackStartedEventHandler (precompute cache-write only, the JF-577 skip), AddToQueueIntentHandler (the JF-578 skip: session-queue-only writer awaiting the shared both-stores writer), PlayNextIntentHandler (same InsertAfterCurrent writer shape, JF-578 family), LaunchRequestHandler (legacy session-queue resume offer), PlayIntentHandler (resume fallback queue[0], same legacy family), ClearQueueIntentHandler (logging-only count read; its purpose is to wipe both stores), ProgressReporter (the shared guard/mirror itself), SessionQueue (passive scan helper whose only callers are the exempt PlaybackStarted and the adopted PlaybackNearlyFinished). A new session-queue reader that neither calls the guard nor is listed fails the test by name.

## Tests (red-first) and verification

Six new tests in QueueRehydrationAdoptionTests (APL section): next and previous each get coherent-device-queue serves-queued-track (both observed RED on both TFMs before the handler change: today's Empty answer has no play directive), stale-queue keeps-Empty, and non-empty-session keeps-today's-answer pins. Full suite `dotnet test` (no --no-build): 4009/4009 passed on net9.0 AND net10.0 (tree also carries the in-flight JF-315 batch changes; +8 tests from this task: 6 APL + 2 roster). `dotnet build -c Release`: 0 warnings, 0 errors. Files changed: AplUserEventHandler.cs, QueueRehydrationAdoptionTests.cs, SessionQueueReaderRosterTests.cs (new).

## Risks

Behavioral widening beyond the wiped shape, identical to the Next/Previous adoption: on a populated session queue with a null now-playing item, a token that is a queue member now stands in for current (previously the tap answered Empty), which is the same coherence rule the intent adopters shipped. The post-playback tap (screen persists after the queue ends) rehydrates and serves the next member when the token is coherent, matching the intent-handler semantics. JF-578's both-stores writer remains open and is the tracked fix for the two writer-shaped exemptions. DOD items 9/10 (/simplify, /code-review) intentionally left to the orchestrator's gates per the task instructions.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 9434e11e). ADOPT verdict: Amazon's Request/Response JSON Reference (scraped live) states context.AudioPlayer is included on all customer-initiated requests with token and offset when the skill was the most recently playing audio; the NowPlaying-screen next/previous tap is exactly that, corroborated in-tree by LaunchRequestHandler's same-family read, and the adoption degrades fail-safe to today's behavior without a token. AplUserEventHandler.HandleNext/HandlePrevious now call ProgressReporter.TryRehydrateSessionQueueFromDevice + ResolveCurrentItemId before the queue read (guard after the tap dispatch switch, so pause/selectItem/show-more taps never pay it; no ctor change needed, the handler already held the DeviceQueueManager), mirroring the intent adopters; response shapes and shouldEndSession unchanged; after a mid-playback restart an APL tap now serves the queue's real adjacent track instead of the false Empty. ANTI-FORGETTEN-CONSUMER MECHANISM: new SessionQueueReaderRosterTests, the WarmingGateCoverageTests IL-scan precedent generalized to property-getter readers (Module.ResolveMethod for the cross-assembly memberref; MethodSpec tokens deliberately skipped, both failure classes fail loud): Fact 1 asserts the 13 discovered NowPlayingQueue readers exactly match the roster (ADOPTED: Next, Previous, ListQueue, PlaybackNearlyFinished, AplUserEventHandler; EXEMPT with one-line reasons: PlaybackStarted precompute, AddToQueue + PlayNext both-stores writers per JF-578, LaunchRequest legacy resume offer, Play resume fallback, ClearQueue logging-only, ProgressReporter the guard itself, SessionQueue passive helper); Fact 2 mechanically proves ADOPTED equals the real set of guard callers so the label cannot rot. Tests: 6 red-first APL adoption pins (coherent serves-queued-track observed RED pre-change on both TFMs, stale-queue keeps-Empty, non-empty-session keeps-today, boundary last/first) + the 2 roster facts. Gates: /simplify 2-agent consolidated pass (R1 applied: all four adjacent-item scans now use SessionQueue.IndexOfQueueItem, the helper that exists for exactly this; efficiency+altitude CLEAN; deferred to JF-582: the combined rehydrate-and-resolve helper, the shared adjacent-item serve extraction, and the roster-scanner dedupe with WarmingGateCoverageTests); code-review high via feature-dev:code-reviewer: no blockers or majors, roster completeness verified against a full 105-hit grep, the one minor (the APL branches bypass the JF-507 codec gate and the JF-564 medium refusal their siblings apply, slightly widened by the adoption) filed as JF-582 same-turn. Suite 4009/4009 both TFMs, Release 0 warnings. JF-569 rode the same commit (doc-only): RepeatIntentHandler class doc corrected; the VideoApp ledger recording exists since JF-563, the residual mid-book misclassification is JF-566.
<!-- SECTION:FINAL_SUMMARY:END -->
