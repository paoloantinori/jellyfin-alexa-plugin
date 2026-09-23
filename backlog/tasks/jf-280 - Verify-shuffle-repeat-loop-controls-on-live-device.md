---
id: JF-280
title: Verify shuffle/repeat/loop controls on live device
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-09-23 14:52'
labels:
  - e2e
  - playback
milestone: m-5
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ShuffleOn/Off, LoopOn/Off, LoopSongOn, and RepeatIntent modify PlaybackInfo state but have never been verified to actually affect Alexa's playback behavior. Need to:
1. Test "shuffle on" during playback — verify next track is random, not sequential
2. Test "loop this song" — verify same track repeats
3. Test "shuffle off" — verify return to sequential order
4. Test interaction between shuffle and repeat modes
5. Verify state persists across pause/resume
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-23 progress (battery evidence, task stays OPEN): the handler-firing + spoken-confirmation legs are verified live — LoopAllOn arrived and answered «Riproduzione in loop attivata» (2026-09-22 19:04:27 corr c5e3fe11), and the shuffle steal is fixed with ShuffleAllOn twins (profile-nlu verified on the deployed model in it/de/fr; device re-test pending Paolo). The BEHAVIORAL effects (items 1-5: next track actually random / same track actually repeats / order restored / mode interplay / persistence) remain unverified — this task now tracks exactly that device session: loop on then let a track END; shuffle on then let 2-3 tracks pass; observe.
<!-- SECTION:NOTES:END -->
