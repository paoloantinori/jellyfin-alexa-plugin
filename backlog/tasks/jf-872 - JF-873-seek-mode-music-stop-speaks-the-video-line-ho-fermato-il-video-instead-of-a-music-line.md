---
id: JF-872
title: >-
  JF-873: seek-mode music stop speaks the video line ("ho fermato il video")
  instead of a music line
status: Done
assignee: []
created_date: '2026-10-12 16:49'
updated_date: '2026-10-16 09:32'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-870 round (worker's code-review finding, deliberately out of that change's scope): when seek-mode music is active (NativeControlsForAudio/VideoAppForAudio ON, the JF-625 machinery that stays shipped but OFF by default), stopping playback by voice speaks VideoStoppedByVoice ("ho fermato il video") because PauseIntentHandler's medium-aware line only distinguishes the AUDIOBOOK branch so far. PlayingMedium.VideoAppAudio should get its own natural line (e.g. it-IT "Ho fermato la musica.") in the same BookStoppedByVoice pattern: key in all 17 locales, branch in the VideoApp-family arm, ledger row, test pin. Low priority: the path is OFF by default after the JF-625 maintainer NO (2026-10-11), so only opt-in users hear the wrong line. Live TV keeps the video line (canonical for a channel stream).
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
EXECUTED together with JF-871 in ONE branch (the two rows are the same defect from different review rounds): branch jf871 commit 46a93a4b, merged to main 676e3bc5, pushed, DEPLOYED and live-verified (see JF-871's record for the full deploy/verification detail; the seek-mode music stop line is MusicStoppedByVoice, it-IT 'Ho fermato la musica.', minted in all 17 locales). Gates recorded in 46a93a4b (build 0 warnings both TFMs, 6018/6018 both TFMs, validate_locales PASS, /simplify 4 agents, /code-review high 4 findings dispositioned, red proof pre-fix). Live TV keeps the video line (pinned); the opt-in path is OFF by default (the JF-625 maintainer NO), so only opt-in users hear the change. ID-collision note: this row's task ID (JF-872) is distinct from CLAUDE.md line 73's '(JF-872, maintainer call 2026-10-13)' PauseKeepsSession citation; disambiguate by context. gate-exempt: gates ran in the dispatched worker's transcript; commit 46a93a4b carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
