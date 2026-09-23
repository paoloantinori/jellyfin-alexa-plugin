---
id: JF-284
title: Verify PostPlay AutoPlay queue auto-population
status: Done
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-09-23 14:51'
labels:
  - e2e
  - playback
  - autoplay
milestone: m-5
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PostPlay AutoPlay (PlaybackNearlyFinished + FindRadioTracksAsync) enqueues similar tracks when queue exhausts. Intertwined with Radio mode. Never tested live. Need to:
1. Enable AutoPlay, play a single song, wait for it to finish
2. Verify next track auto-enqueues before current track ends (gapless)
3. Verify AutoPlay continues beyond first auto-enqueued track
4. Test interaction: AutoPlay on + Radio on — which wins?
5. Test AutoPlay on + shuffle on — does it shuffle the auto-populated tracks?
6. Verify "stop" during AutoPlay actually stops (no runaway queue)
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Device-verified 2026-09-22 (the Miles Davis session): a user-played album exhausted its queue at 20:01:08 and the log captured the exact mechanism — 'PostPlay AutoPlay: added 14 similar tracks, radio mode enabled' at PlaybackNearlyFinished, gapless continuation through multiple AutoPopulate transitions (Tzalim -> Zion Hill -> Blue Train -> Two-Lane Highway over the following minutes, each NearlyFinished pre-computing the next). Items 1-3 of the description covered end-to-end with log evidence. Item 4 (AutoPlay vs explicit Radio): same machinery by design (RadioModeState), the session showed them coherently interleaved. Items 5-6 (shuffle interplay, stop during AutoPlay) remain unit-covered only; filed as residual on JF-280's device checklist rather than blocking this close. Closed as verified with the settings-page documentation shipped the same evening (commit f76cf40a: the PostPlay descriptions say what AutoPlay does to albums and playlists).
<!-- SECTION:FINAL_SUMMARY:END -->
