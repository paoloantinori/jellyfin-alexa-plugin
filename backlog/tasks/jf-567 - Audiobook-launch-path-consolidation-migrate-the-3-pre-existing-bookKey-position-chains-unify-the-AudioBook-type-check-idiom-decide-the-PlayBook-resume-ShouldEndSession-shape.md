---
id: JF-567
title: >-
  Audiobook launch-path consolidation: migrate the 3 pre-existing
  bookKey/position chains, unify the AudioBook type-check idiom, decide the
  PlayBook resume ShouldEndSession shape
status: Done
assignee: []
created_date: '2026-09-15 05:17'
updated_date: '2026-09-17 14:56'
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

JF-567 execution (2026-09-17), per-item verdicts:

1. DONE. All three inline bookKey/tracker chains migrated to ResumeMath.GetAudiobookBookKey + GetAudiobookStartTicks: PlayBookIntentHandler (fresh-launch branch), YesIntentHandler UseResumePlaylist arm (fallback = the offered offset ticks), LaunchRequestHandler read-only variant (GetAudiobookStartTicks(bookKey, 0), then the > 0 override check, byte-equivalent to the old GetPositionTicks ?? 0 read). JF-581 reconciliation: ResolveResumeTicks (the store fallback, UserData first then ItemPositionState, never over Played) and the tracker read are COMPLEMENTARY layers, both kept; precedence documented in the LaunchRequestHandler comment: the tracker wins only when > 0 (a capable device played the book on the HLS concat timeline the store cannot see), otherwise the store-resolved ticks stand. The BuildAudiobookResumeResponse parentId ternary is NOT unified: it resolves a URL path segment, where the default dashed Guid format is correct and the record path (dashed URL itemId) vs read path ("N" bookKey) agree only because AudiobookPositionTracker.NormalizeKey canonicalizes both; comment added at the ternary documenting this.

2. DONE (extracted, common core). The three fresh-launch compositions (PlayBook fresh branch, YesIntent JF-361 PlayBook confirmation, StartOver book restart) are identical modulo the announce speech, so the shared builder BuildAudiobookVideoAppLaunchResponseAsync now owns the BuildVideoAppAudioResponse call plus the JF-501 progressive-announce attachment and its screenless-degradation rationale once (on PlaybackLaunchBuilder, beside the family). One deliberate nuance documented here: YesIntent previously passed `locale` as BuildVideoAppAudioResponse's announceLocale; on this path that parameter was dead (AttachAnnounceIfEnabled's output is always overwritten by the caller's announce attachment), so the shared builder does not forward it.

3. DONE. All five GetType().Name.Equals("AudioBook", Ordinal) sites (SearchMediaIntentHandler, LaunchRequestHandler, DynamicEntityBuilder, PlaybackLaunchBuilder x2) now use AudiobookItems.IsAudioBook (new Alexa/Util/AudiobookItems.cs, the `is MediaBrowser.Controller.Entities.AudioBook` form proven in production by YesIntent JF-361 routing and the JF-563 branches). Both stale "concrete type is in the server assembly, not the controller package" comments (SearchMediaIntentHandler, DynamicEntityBuilder) were removed: the type IS in the controller package (YesIntent's plain `item is AudioBook` with using MediaBrowser.Controller.Entities compiles and runs in production).

4. DECIDED: OMIT (null). Evidence: the true was introduced by commit ef30a07a (2026-06-14, the audiobook HLS resume feature) as an incidental copy from the sibling AudioPlayer resume path in the same commit (which legitimately keeps true per the play-true rule); no incident, bug, or test in history pins the VideoApp-resume true (git log -S over the file shows only ef30a07a and the JF-315 mechanical moves). The repo CLAUDE.md reference is binding ("VideoApp.Launch responses: never set shouldEndSession"), BuildAudiobookResumeResponse itself builds ShouldEndSession = null, and the two sibling resume branches (YesIntent, ResumeIntentHandler JF-563) omit it; setting true on a VideoApp launch response forfeits the documented 30-second closed-mic session window for no benefit. PlayBook's AudioPlayer resume path below keeps its play-true shape (JF-299). Pinned by PlayBookIntentHandlerTests.HandleAsync_BookWithProgress_NativeControls_ResumePlaylist_OmitsShouldEndSession (asserts the VideoApp directive, start= URL, null ShouldEndSession, and the resuming announce).

5. DONE. Both hand-built AudiobookPositionTracker fixtures in VideoAudioControllerTests (GetSegment_FolderBackedItem_RecordsPositionTracking, GetSegment_EpisodeItem_SkipsPositionTracking_RestartSafe) now use the TestHelpers.CreatePositionTracker factory (via the file's `using static` import).

Verification: dotnet build both TFMs 0 warnings (Debug and Release), dotnet test 4046 passed / 0 failed / 0 skipped on EACH of net9.0 and net10.0 (includes the new pinning test; items 6 and 7 above stay deferred as designed).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commits 14935808 + 2856cdcd). (1) The three inlined bookKey/tracker chains (PlayBook, YesIntent useResumePlaylist arm, LaunchRequest read-only) route through ResumeMath.GetAudiobookBookKey/GetAudiobookStartTicks with the JF-581 reconciliation documented (tracker and the store fallback are complementary layers, tracker wins when >0); the BuildAudiobookResumeResponse parentId ternary stays dashed-Guid deliberately (URL segment vs the NormalizeKey-canonicalized read, documented in place). (2) The three fresh-launch sites compose through the new PlaybackLaunchBuilder.BuildAudiobookVideoAppLaunchResponseAsync (JF-501 progressive announce and the screenless-degradation rationale owned once; YesIntent's dead announceLocale dropped). (3) AudioBook detection unifies behind Alexa/Util/AudiobookItems.IsAudioBook (the is-form; ALL sites migrated in the closure pass - the 5 string-comparison ones plus the 3 remaining raw is-sites the /simplify review found - and the two stale server-assembly comments removed). (4) DECIDED per the binding reference: PlayBook's VideoApp resume ShouldEndSession=true was an incidental 2026-06-14 copy from the sibling AudioPlayer path (no incident, no pin; git log -S verified), violating the reference and forfeiting the 30s closed-mic window; now null, pinned by test; the review verified the JF-387 interceptor consequence (attributes now copy on this in-session shape, the standing exposure of the whole VideoApp family) and that the AudioPlayer resume keeps play-true per JF-299. (5) Both tracker test fixtures migrated to CreatePositionTracker. REVIEW MAJORS (pre-existing bugs on this exact chain, fixed in the follow-up commit): the ResumeIntent session-tail passed MILLISECONDS into the helper's ticks fallback (a 45-minute resume became 4.5 minutes; the call site now converts once mirroring the pinned YesIntent idiom), and the cold-tracker fallback sliced the whole-book concat timeline with CHAPTER-relative ticks (chapter 12 at 20min landed inside chapter 1); both resume paths now gate on the TRACKER: warm slices, cold-with-chapter-progress flat-resumes the chapter at its own offset (the LaunchRequest offer discipline), genuinely-fresh keeps the fresh VideoApp launch. The bug-pinning test (it asserted the server-progress slice) rewritten to the corrected contract. Minor noted, not fixed (pre-existing asymmetry): PlayBook's and YesIntent's resume announces ride the final response instead of the JF-501 progressive vehicle. Items 6 (chapter-selection degrade) and 7 (pre-tail token guard) remain deferred in the notes as scoped. Gates: /simplify 4-angle pass (S1-S4 applied: the stale migration doc, the three remaining is-sites + doc claim scoped, unqualified symbol, log template; R1/R2/S5 skipped as optional with reasons); code-review high via feature-dev:code-reviewer: the ShouldEndSession flip verified correct and required, both majors applied, all hunt questions answered clean. Suite 4046/4046 both TFMs, Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
