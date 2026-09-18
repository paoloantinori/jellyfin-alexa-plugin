---
id: JF-566
title: >-
  Repeat during VideoApp audiobook playback restarts the previous audio track
  (book launches are not recorded as device last-played)
status: Done
assignee: []
created_date: '2026-09-15 04:18'
updated_date: '2026-09-18 18:43'
labels:
  - bug
  - audiobook
  - playback
  - repeat
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-562 code review. RepeatIntentHandler resolves the current item from context.AudioPlayer.Token, then session.FullNowPlayingItem, then the DeviceQueueManager last-played record. A VideoApp-launched audiobook (NativeControlsForBooks on) is recorded NOWHERE: PlayBookIntentHandler launches books via BaseHandler.BuildVideoAppAudioResponse (no RecordLastPlayed call) and LastPlayedResponseInterceptor deliberately skips the audiobook concat URL shape to preserve chapter accuracy (its remarks, Pipeline/LastPlayedResponseInterceptor.cs). So a repeat arriving mid-book resolves the PREVIOUS music track (stale token) and restarts it via AudioPlayer.Play, destroying nothing audible (the book keeps streaming) but answering nonsense.

Candidate fix directions (need design care because the recording skip is deliberate):
- Record the book PARENT id in the audiobook launch path (not in the interceptor's URL parsing, which cannot distinguish book concat from other shapes without weakening the chapter-precision rule) so GetLastPlayedItemId returns the book mid-play; RepeatIntentHandler would then answer the honest CannotRepeatContent tell.
- Check interactions first: LaunchRequestHandler's no-token resume-offer path (BuildDeviceLastPlayedOffer) would start offering the BOOK on skill open for devices whose last play was a book; verify that path handles AudioBook items correctly (it may already, via FindLastPlayedItemWithProgress).

Currently documented as a Known limitation in RepeatIntentHandler's class doc.
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
FIXED (commit 5ddbf2bf). The task's original premise (VideoApp book launches recorded nowhere) was already superseded by JF-563's ledger centralization (both VideoApp book recording sites, PlaybackLaunchBuilder ~850 and ~1483, record the chapter/single-file AudioBook-kind id with route VideoApp since JF-568); the residual delta this task closed is the misclassification: RepeatIntentHandler's videoDisplacedAudio rule accepted only IsVideoAppLaunchItem kinds (movie/episode/channel), so a repeat arriving mid-VideoApp-book resolved the STALE music token and restarted the wrong track. Fix: the rule extends to `|| AudiobookItems.IsAudioBook(lastPlayedItem)`, with the JF-568 route guard keeping a FLAT audio-path book (route Audio) on token-first resolution where its own token names it. Once the book wins resolution, the pre-existing AudioBook exclusion (item is Audio && !IsAudioBook) lands on the honest CannotRepeatContent tell. The class doc's Known limitation updated to the FIXED state (superseding the JF-569 interim wording). Tests: 2 new in RepeatIntentHandlerTests - HandleAsync_VideoAppRoutedBookLedger_StaleMusicToken_AnswersCannotRepeatForBook (red-first PROVEN by stash: 1/1 failed without the change on both TFMs, green with) and HandleAsync_FlatAudioBookLedger_RouteAudio_KeepsTokenFirstResolution (the control pinning the route guard). GATES: /simplify 4-angle + adversarial combined pass - zero correctness findings (the audio-branch interplay verified load-bearing; no worse-answer sub-shape enumerated; the route-null legacy book converges to the same CannotRepeatContent tell; both production recording sites verified to record the AudioBook-kind chapter id). Code-review high via feature-dev:code-reviewer on the committed state: CLEAN at confidence >= 80 - the composite sleep-suffixed-token divergence traced outcome-invariant (music tokens never carry book/video kinds), the DI wiring verified live, both tests verified discriminating; two below-threshold notes (confidence 25-30, no defect): the raw token inequality vs ResolvePlayingMedium's codec-canonicalized check is a latent divergence only if a future suffix lands on a book/video token, and an Enqueue-after-VideoApp-book shape answers CannotRepeatContent consistently with the established JF-564 family semantics (not a regression). One LOW reuse observation RECORDED not acted on: the route+kind encoding now exists twice (the handler's local mirror and ResolvePlayingMedium's kind section) - consolidation is constrained by documented contracts (books must NOT enter IsVideoAppLaunchItem; Repeat keeps its own resolution), and the local-mirror shape is the codebase's intended pattern. Suite 4096/4096 both TFMs, Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
