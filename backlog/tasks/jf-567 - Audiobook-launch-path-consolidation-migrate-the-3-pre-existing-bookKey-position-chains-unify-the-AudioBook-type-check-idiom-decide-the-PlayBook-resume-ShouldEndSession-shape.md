---
id: JF-567
title: >-
  Audiobook launch-path consolidation: migrate the 3 pre-existing
  bookKey/position chains, unify the AudioBook type-check idiom, decide the
  PlayBook resume ShouldEndSession shape
status: To Do
assignee: []
created_date: '2026-09-15 05:17'
updated_date: '2026-09-15 05:43'
labels:
  - tech-debt
  - audiobook
  - refactor
dependencies: []
references:
  - >-
    backlog/tasks/jf-563 -
    Resume-and-StartOver-of-audiobooks-bypass-NativeControlsForBooks-flat-audio-stream-instead-of-the-VideoApp-HLS-path.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Consolidation follow-up out of the JF-563 review (2026-09-15). JF-563 added GetAudiobookBookKey(item) and GetAudiobookStartTicks(bookKey, fallbackTicks) (canonical home since JF-315 batch 2: Alexa/Util/ResumeMath.cs, moved verbatim out of BaseHandler) and used them in ResumeIntentHandler/StartOverIntentHandler, but the pre-existing copies of the same chains were left untouched to keep the diff scoped. All items below are mechanical, no behavior change intended:

1. Book key + tracker-position chain still inlined at pre-existing sites; migrate to the new helpers: PlayBookIntentHandler.cs ~270-277 (bookId ternary + tracker read + resumeTicks fallback), YesIntentHandler.cs ~213-220 (same, fallback = offered offset), LaunchRequestHandler.cs ~253-255 (read-only variant), and the parentId ternary inside BaseHandler.BuildAudiobookResumeResponse (~1950, same resolution without the "N" format). Drift here fails silently (resume falls to position 0), which is why the key shape now has one definition.
2. Book VideoApp launch composition (AudioBook+flag gate, BuildVideoAppAudioResponse, SpeakVideoLaunchAnnounceAsync announce) is hand-rolled at PlayBookIntentHandler ~286-305, YesIntentHandler ~359-366, and StartOverIntentHandler; consider one shared builder owning the screenless-degradation comment once.
3. AudioBook detection idiom split: 5 sites use item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal) (BaseHandler ~1326 and ~1542, LaunchRequestHandler ~249, SearchMediaIntentHandler ~519, DynamicEntityBuilder ~445) while RepeatIntentHandler and the JF-563 branches use `is MediaBrowser.Controller.Entities.AudioBook` (proven in production). Unify behind one predicate; the `is` form is preferred. Also verify whether SearchMediaIntentHandler's "concrete AudioBook type is in the server assembly" comment is still current.
4. PlayBookIntentHandler ~295 sets ShouldEndSession=true on the sliced-playlist VideoApp resume response; the repo CLAUDE.md reference ("VideoApp.Launch responses: never set shouldEndSession") and the YesIntent + JF-563 resume branches omit it. Decide one shape (reference argues omit; PlayBook is the outlier) and pin it.
5. VideoAudioControllerTests ~4153-4171 and ~4197-4213 build AudiobookPositionTracker fixtures by hand; migrate to the new TestHelpers.CreatePositionTracker factory (added by JF-563).
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
Added from the JF-563 high-effort code review (2026-09-15), both deliberately deferred from JF-563:

6. BuildAudiobookResumeResponse's screenless degrade now CLAMPS the book-absolute tracker position to the chapter runtime (JF-563 interim mitigation). The honest fix is chapter SELECTION: on degrade, resolve which chapter contains the book-absolute position and flat-play that chapter at the within-chapter offset (mirrors PlayBook's FindResumeTrackIndex walk). Tracked here because it needs the sibling-query design, not a clamp.

7. ResumeIntentHandler's pre-tail book guard requires token-empty OR token==FullNowPlayingItem.Id, so the shape (token present from an earlier flat book play, FullNowPlayingItem cleared by PlaybackStopped) resumes flat without the slice. Requires a flag flip or pre-deploy play to reach; fix would resolve the token item via ILibraryManager and type-check it. No wrongly-takes shape exists (review-verified).

RESOLVED within JF-563 itself (no longer needed here): the device last-played ledger regression the review's P2 named. The record was centralized into the capable-device arms of BuildVideoAppAudioResponse and BuildAudiobookResumeResponse (idempotent via RecordLastPlayed's same-item short-circuit), which also closes the pre-existing PlayBook/YesIntent holes this task was going to file.
<!-- SECTION:NOTES:END -->
