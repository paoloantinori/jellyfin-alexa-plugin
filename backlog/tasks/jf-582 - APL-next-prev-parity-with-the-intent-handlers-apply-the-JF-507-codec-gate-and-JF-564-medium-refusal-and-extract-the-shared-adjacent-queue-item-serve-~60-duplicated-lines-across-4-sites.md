---
id: JF-582
title: >-
  APL next/prev parity with the intent handlers: apply the JF-507 codec gate and
  JF-564 medium refusal, and extract the shared adjacent-queue-item serve (~60
  duplicated lines across 4 sites)
status: Done
assignee: []
created_date: '2026-09-16 21:22'
updated_date: '2026-09-17 05:06'
labels:
  - reliability
  - refactor
  - queue
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-579 gates (2026-09-16). Code-review minor (pre-existing, widened by the rehydration adoption): AplUserEventHandler.HandleNext/HandlePrevious launch adjacent queue items via Launch.GetStreamUrl directly, bypassing BOTH the JF-507 codec gate (Launch.ResolveAudioLaunchSource: an EAC3-family video item as queue successor routes to the audio-only transcode instead of dying on raw static bytes) and the JF-564 VideoApp-medium refusal that NextIntentHandler/PreviousIntentHandler apply. Reach is narrow (the APL NowPlaying screen implies audio was the most recent play) but a cross-media queue with a video successor plus an APL next tap now executes the launch path on rehydrated content and would serve an unplayable stream. /simplify S1+S2: the guard-plus-resolve pair is now pasted at four sites (the rehydrated local is dead except as the resolve argument; a combined TryRehydrateAndResolveCurrentItemId helper would couple them), and the four adjacent-item bodies (Next/Previous intent handlers + APL HandleNext/HandlePrevious) became shape-identical at entry with three drift dimensions already present (no medium refusal, no codec gate, no logging on the APL side); a shared ServeAdjacentQueueItem(session, user, context, direction) on ProgressReporter would delete ~60 duplicated lines and remove the drift surface. Scope when picked: extract the shared adjacent-item serve WITH the JF-507 gate and JF-564 refusal applied uniformly to all four call sites, red-first tests for the APL video-successor case (transcode route instead of raw static), and consider the combined rehydrate-and-resolve helper. Also accepted residue from the JF-579 /simplify pass (T1): SessionQueueReaderRosterTests duplicates ~30 lines of the WarmingGateCoverageTests IL-scanner (ContainsCallToToken vs ContainsGateCall, DeclaredCallableMethods); a shared parameterized scanner in the test project would dedupe, rides here since it touches the same test family. Related: JF-574, JF-577, JF-579, JF-507, JF-564.
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
Session 2026-09-17 (implementation; status left In Progress, tree left uncommitted for the orchestrator's gates).

## Extraction home and signature

The serve lives on ProgressReporter (it already hosts the rehydration family, holds the same Launch collaborator instance the handlers use, and its logger IS the handler's logger, so the moved log lines keep their category). `internal SkillResponse ServeAdjacentQueueItem(DeviceQueueManager? queueManager, ILibraryManager libraryManager, SessionInfo session, Context context, Entities.User user, string locale, AdjacentQueueDirection direction, string logLabel)` with the nested `internal enum ProgressReporter.AdjacentQueueDirection { Next, Previous }`. All per-site dependencies arrive as parameters (the JF-522 per-call queueManager idiom preserved; no ctor changes anywhere). The combined `public static Guid? TryRehydrateAndResolveCurrentItemId(queueManager, session, context, logger, logLabel)` couples the guard and its current-item companion so the `rehydrated` hand-off cannot be dropped by a future call site; guard-only consumers (ListQueue, PlaybackNearlyFinished) keep calling the two members directly.

## Per-site behavior deltas

- NextIntentHandler and PreviousIntentHandler: `HandleAsync` is now a one-line delegation. NO behavior change intended or observed: the serve reproduces the exact gate order (medium refusal, rehydration, empty check, scan with the per-direction edge predicate, library resolve, FullNowPlayingItem update, codec-gated launch), the exact response shapes, and byte-identical log text (`{logLabel}: ...` with the handler name as the label). Their existing tests (VideoAppGapHonestResponseTests, QueueRehydrationAdoptionTests) pass UNCHANGED, as pinned.
- AplUserEventHandler next/prev taps: HandleNext/HandlePrevious deleted; the switch cases call the serve directly. Behavior legitimately WIDENS (the point of the task): (a) the JF-564 VideoApp-medium refusal now applies (a movie/live-TV last-played ledger entry answers the localized CannotNavigateVideoByVoice Tell instead of emitting a misdirected AudioPlayer.Play mid-video; a VideoApp audiobook keeps the silent Empty); (b) the JF-507 codec gate now applies (a Movie/Episode queue successor with an Echo-undecodable audio codec routes to the audio-only episode HLS transcode instead of the raw static `/Audio/{id}/stream` bytes; audio items and decodable video keep the identical static URL, since ResolveAudioLaunchSource returns GetStreamUrl on that route); (c) the debug-logging drift is closed (the taps now log the same entry/refusal/empty/edge/not-found/playing lines under the "AplUserEvent next"/"AplUserEvent previous" labels). No shouldEndSession change on any branch: Empty, the refusal Tell, and the AudioPlayer.Play build (ShouldEndSession=true per JF-299, owned by BuildAudioPlayerResponse) are exactly the pre-existing shapes.

## Roster and scanner notes

SessionQueueReaderRosterTests rosters updated to the new call topology (a NECESSARY consequence of the extraction, not a gate failure): the three adjacent-item types no longer read `session.NowPlayingQueue` directly (the serve does), so AdoptedReaders is now ListQueueIntentHandler + PlaybackNearlyFinishedEventHandler, and the guard-caller test excludes ProgressReporter as the owner (it calls the guard internally via the combined helper and the serve; its ExemptReaders reason documents this). The behavioral lock for the trio is QueueRehydrationAdoptionTests plus the four new JF-582 tests. WarmingGateCoverageTests: ExpectedGatedHandlers UNCHANGED. The T1 scanner dedupe landed as `Jellyfin.Plugin.AlexaSkill.Tests/Handler/IlCallScanner.cs` (shared DeclaredCallableMethods, CallTokens, ContainsCallToToken/ContainsCallToAnyToken, CallsGetter, TopLevelType); both roster files point at it and stay green.

## Tests (red-first) and verification

Four new tests in AplUserEventHandlerTests, all observed RED on both TFMs before the implementation (the transcode pair failed on the raw static /Audio/ URL being served; the refusal pair failed on no speech at all): HandleAsync_NextAction_VideoSuccessorWithEac3_RoutesToAudioOnlyTranscode, HandleAsync_PrevAction_VideoPredecessorWithEac3_RoutesToAudioOnlyTranscode, HandleAsync_NextAction_VideoAppMediumPlaying_AnswersTransportRefusal, HandleAsync_PrevAction_VideoAppMediumPlaying_AnswersTransportRefusal. Full suite `dotnet test` (no --no-build): 4013/4013 passed on net9.0 AND net10.0. `dotnet build -c Release`: 0 warnings, 0 errors. Files changed: ProgressReporter.cs, NextIntentHandler.cs, PreviousIntentHandler.cs, AplUserEventHandler.cs, AplUserEventHandlerTests.cs, SessionQueueReaderRosterTests.cs, WarmingGateCoverageTests.cs, IlCallScanner.cs (new).

## Risks

The APL refusal is a Tell (session-ending) on a UserEvent; that is the same response class the intent handlers already use for the same refusal and UserEvent requests are not AudioPlayer events, so the JF-299 event restriction does not apply. The refusal's reach on the APL path is narrow by construction (the NowPlaying screen implies a recent audio launch, and a token matching the ledger item classifies Audio and skips the refusal); the widened gate only fires on the cross-media shapes the task targeted. No interaction-model or locale change (DoD 6/8 not applicable). DoD 7's E2E angle is covered at unit level here; the live E2E suite is the orchestrator's deploy-gate call. DoD 9/10 (/simplify, /code-review high) intentionally left to the orchestrator's gates per the task instructions.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 2953b1f0). The four next/previous entry points route through one shared serve: ProgressReporter.ServeAdjacentQueueItem(queueManager, libraryManager, session, context, user, locale, AdjacentQueueDirection, logLabel, tapOrigin=false), internal, with the nested AdjacentQueueDirection enum mirroring the PlayingMedium precedent. It applies uniformly: the JF-564 medium refusal (ResolvePlayingMedium + BuildVideoAppTransportRefusal), the JF-577/579 rehydration guard + current-item resolution via the combined TryRehydrateAndResolveCurrentItemId (PRIVATE by the /simplify finding: a public wrapper would be a door that skips the gates; the unsplittable-pair rationale survives as its doc), the JF-507 codec-gated launch (ResolveAudioLaunchSource), SessionQueue.IndexOfQueueItem, and the JF-299-compliant AudioPlayer.Play build. NextIntentHandler and PreviousIntentHandler are one-line delegations with byte-identical gate order, edge predicates, response shapes and log text (VideoAppGapHonestResponseTests and QueueRehydrationAdoptionTests pass unchanged). AplUserEventHandler's tap cases route through the serve and legitimately widen: the refusal and the codec gate now apply where a raw static URL was served for a video-family successor (4 red-first tests: transcode routing observed RED serving /Audio/ static, refusal observed RED with no speech). Review-driven: TAP origins answer the Video/LiveTv refusal arms with a silent session-ending Empty (the voice Tell's 'use the touchscreen' wording tells a touch user to do what they just did); the two refusal tests moved to that silent contract. Roster consequence handled explicitly: AdoptedReaders is now ListQueue + PlaybackNearlyFinished, ProgressReporter exempt as the serve owner, and the guard-caller proof strips only the two OWNING METHODS' calls (per-method, with an existence assertion that fails loudly on rename), so a future ungated ProgressReporter queue reader still fails both roster facts. IlCallScanner extracted as the shared test-project IL scanner; both roster files deduped (~50 lines), both suites green. Gates: /simplify consolidated 4-angle pass (S1/A1 private helper + A2 charter widened applied; the two-scan skeleton duplication left as-is); code-review high via feature-dev:code-reviewer: no blockers or majors, both minors applied (per-method exemption, tap-silent refusal). Suite 4013/4013 both TFMs, Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
