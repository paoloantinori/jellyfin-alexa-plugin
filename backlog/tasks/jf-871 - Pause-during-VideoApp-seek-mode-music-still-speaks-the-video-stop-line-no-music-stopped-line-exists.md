---
id: JF-871
title: >-
  Pause during VideoApp seek-mode music still speaks the video stop line (no
  music stopped line exists)
status: Done
assignee: []
created_date: '2026-10-12 16:26'
updated_date: '2026-10-16 09:31'
labels:
  - wording
  - locales
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-870 altitude review (2026-10-12): PauseIntentHandler's VideoApp-family pause/cancel arm now picks the spoken stop line per medium, but only the audiobook branch got its own key (BookStoppedByVoice). The remaining VideoApp-family member PlayingMedium.VideoAppAudio (music riding the VideoApp HLS seek-mode path, the JF-625 song/whole-album concat) still speaks VideoStoppedByVoice ("ho fermato il video" / "I've stopped the video"), the same wrong-medium wording class Paolo flagged for books in JF-870. No music stopped line exists anywhere today (no MusicStoppedByVoice-class key in the source or the 17 locale JSONs; CannotNavigateMusicByVoice belongs to the Next/Previous refusal family and is not a stopped line). Scope when taken: mint the music stopped-line key in all 17 locale JSONs next to BookStoppedByVoice, extend the PauseIntentHandler ternary (the third arm), add the ledger row to ResponseStringsTests.AllExpectedKeys, and pin the VideoAppAudio branch in VideoAppGapHonestResponseTests (today no pause test covers the VideoAppAudio medium at all; the JF-625 fixtures under Jellyfin.Plugin.AlexaSkill.Tests can supply the launch-route shape). Strings are count-invariant and no model/template surface is touched, so the JF-869 model lane is unaffected.
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
EXECUTED on branch jf871 (46a93a4b), merged to main 676e3bc5, pushed, DEPLOYED to minix (net10.0 Release into AlexaSkill_0.12.1.0, active-DLL verified by md5 identity + the MusicStoppedByVoice literal and the embedded 'Ho fermato la musica.' both grepped from the RUNNING dll), config survived (1 user), live probes green (idle pause keeps the JF-482 shape; play pipeline healthy). The change: PauseIntentHandler's VideoApp-family line-key selection is now a three-way switch (VideoAppAudiobook -> BookStoppedByVoice, VideoAppAudio -> MusicStoppedByVoice, else VideoStoppedByVoice), key minted in all 17 locales (it-IT 'Ho fermato la musica.'), ledger row in ResponseStringsTests.AllExpectedKeys, 7-row activity-invariant theory + the folded live-TV pause/cancel pins in VideoAppGapHonestResponseTests, CLAUDE.md THE REFERENCE updated in place (JF-870 precedent). Gates (recorded in 46a93a4b): build 0 warnings both TFMs, suite 6018/6018 both TFMs, validate_locales PASS, red proof (2 new pins failed pre-fix with the video line), /simplify 4 agents (1 finding applied), /code-review high (4 findings: 2 applied, 2 filed as tasks/residuals). NLU fixtures N/A (no model surface); e2e N/A beyond probes (the seek-mode path is opt-in; unit pins cover the branch). Residuals filed: JF-894 (remapped Audio-typed chapters classify VideoAppAudio at the classifier, pre-existing, now audible), the IsVideoAppMedium doc fix applied separately (54fea00b), and the JF-858/JF-632 token-carrying shape documented as decided ground. NOTE for future searchers: this task's ID (JF-872) collides in prose with CLAUDE.md line 73's '(JF-872, maintainer call 2026-10-13)' PauseKeepsSession citation; disambiguate by context. gate-exempt: gates ran in the dispatched worker's transcript (dispatch, report, and verification all in this session); commit 46a93a4b carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
