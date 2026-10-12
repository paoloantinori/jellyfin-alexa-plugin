---
id: JF-871
title: >-
  Pause during VideoApp seek-mode music still speaks the video stop line (no
  music stopped line exists)
status: To Do
assignee: []
created_date: '2026-10-12 16:26'
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
