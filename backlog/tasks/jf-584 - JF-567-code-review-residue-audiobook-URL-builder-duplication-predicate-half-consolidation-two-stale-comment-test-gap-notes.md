---
id: JF-584
title: >-
  JF-567 code-review residue: audiobook URL builder duplication, predicate
  half-consolidation, two stale-comment/test-gap notes
status: To Do
assignee: []
created_date: '2026-09-17 16:12'
labels:
  - review-residue
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residue from the /code-review high pass on the JF-567 changeset (2db7a6d6..9d9cd744), cut at the 10-finding output cap and filed here per the same-turn discipline. All four items are small cleanups in the audiobook launch area; one focused PR can carry them. The full review's ranked findings live in the review transcript; the substantive findings are NOT in this task (only these cleanups were cut).

1. DUPLICATED AUDIOBOOK URL MINTERS (reuse). Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs: GetAudiobookResumeUrl (~line 103) builds the same endpoint path + StreamTokenHelper.Mint(parentId) token as GetAudiobookVideoAudioUrl (~line 645); they differ only by the ?start= query parameter. A future route/token-shape change edited in one and missed in the other breaks every resume while first-play keeps working. Fix: one URL builder with an optional startTicks parameter.

2. HALF-DONE PREDICATE CONSOLIDATION (reuse). Alexa/Util/AudiobookItems.cs was added as the one audiobook predicate, but bare `is AudioBook` checks remain in production (YesIntentHandler ~line 150 JF-361 routing, ResumeIntentHandler ~line 378 TryBuild gate). Either migrate those to AudiobookItems.IsAudioBook or drop the wrapper and use the type pattern inline everywhere; the helper's own doc admits the split.

3. STALE FRESH-LAUNCH COMMENT (docs). PlayBookIntentHandler ~line 279: the cold-tracker `if (resumeTicks <= 0)` fresh-launch branch comment says it serves "a genuinely FRESH book (no chapter progress)", but FindResumeTrackIndex also returns resumeTicks==0 for the (lastPlayedIndex+1, 0) shape (earlier chapters fully played, next unstarted), where startIndex>0 is silently discarded by the VideoApp fresh launch. Behavior is unchanged from pre-JF-567 and acceptable; the comment should describe the actual shape.

4. SIGN-ONLY CONVERSION (test gap note). ResumeIntentHandler ~line 298: the ms-to-ticks conversion feeding TryBuildNativeControlsBookResumeAsync's fallbackTicks is only ever sign-tested inside the helper (the value is never used), and no test covers the session tail's cold-tracker-with-offset path. If someone later uses fallbackTicks as a slice value, a units bug ships uncaught; either add the tail-path test or note the sign-only contract at the parameter.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 GetAudiobookResumeUrl and GetAudiobookVideoAudioUrl share one URL-building implementation (only ?start= differs)
- [ ] #2 Every production audiobook type check goes through AudiobookItems.IsAudioBook, or the wrapper is removed and the type pattern is used inline consistently (grep shows one form)
- [ ] #3 The PlayBook cold-tracker fresh-launch comment describes the (lastPlayedIndex+1, 0) shape accurately
- [ ] #4 The fallbackTicks sign-only contract is either covered by a session-tail cold-tracker test or documented at the TryBuildNativeControlsBookResumeAsync parameter
- [ ] #5 dotnet build 0 warnings, dotnet test green (no --no-build)
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
