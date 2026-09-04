---
id: JF-480
title: >-
  Radio station elicit skipped when playback is merely PAUSED (session
  now-playing survives pause; radio mode starts from the paused track)
status: Done
assignee: []
created_date: '2026-09-04 09:58'
updated_date: '2026-09-04 10:55'
labels: []
dependencies: []
references:
  - 'Device corr=2c2d8676 (2026-09-04, paused-then-jazz sequence)'
  - JF-472 (the elicit whose branch model missed the paused state)
  - CLAUDE.md FullNowPlayingItem vs AudioPlayer.Token gotcha
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 batch re-test (corr=2c2d8676): after PAUSING a track (AMAZON.PauseIntent at 11:54:46, PlaybackStopped fired), 'suona jazz' 43 seconds later arrived as PlayRadioIntent with an empty station slot — and the JF-472 elicit did NOT fire: the handler took the something-playing branch and started radio mode from the paused track ('In riproduzione Delicate. Modalità radio attiva. Trovati 20 brani simili.'). The session's now-playing state (FullNowPlayingItem / PlayState) still reported the paused item as playing.

Root cause class: PAUSED is a third state the JF-472 acceptance criteria did not model (they assumed playing/not-playing binary). Jellyfin's PlaybackStopped event on PAUSE (the plugin's own BuildPauseResponse emits AudioPlayer.Stop, so the platform sends PlaybackStopped) apparently does not clear the session now-playing state the radio handler reads — or the handler reads a source that survives pause (FullNowPlayingItem vs context.AudioPlayer; note the CLAUDE.md gotcha that FullNowPlayingItem is cleared by PlaybackStopped in some flows: verify which reader the radio handler uses and what actually survived the pause here).

Fix direction (decide by evidence): the radio handler's playing-check should treat PAUSED as not-actively-playing (check the session PlayState == Playing, not just item presence), so a paused device gets the station elicit. Keep the genuinely-playing branch byte-identical. Unit tests: empty slot + paused session (PlayState=Paused, NowPlayingItem set) -> elicit; empty slot + actively playing -> radio mode unchanged; empty slot + fully idle -> elicit (existing). Device re-verification: pause a track, then 'suona jazz' -> the station question.
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
Implemented and deployed (commit 397be652). Live verification is device-only (adjudicated): the paused-context shape is not producible through simulate-skill (its synthetic context carries no AudioPlayer), so the unit tests pinning the REAL post-pause device shape are the verification level; Paolo's device re-test closes the loop.

Root cause (device corr=2c2d8676, evidence from logs + Jellyfin v10.11.11 source): the album play path optimistically writes FullNowPlayingItem on the COORDINATOR Echo's session before issuing play; in a multi-room group the PlaybackStarted/Stopped reports land on a MEMBER device's session (AMAWQ5 vs AMA22QHY), so the pause cleared the wrong session and the coordinator's optimistic item survived. Item presence therefore cannot distinguish playing from paused; the request context's playerActivity can (STOPPED on every post-pause request; PLAYING/BUFFER_UNDERRUN while active, the same semantics ResumeIntentHandler and PlaybackFinishedEventHandler already use).

Fix: the JF-472 elicit condition becomes !activelyPlaying || no item: paused behaves like idle (the station question, session open), actively playing keeps the byte-identical radio branch, cancel hatch/station-given/feature gate untouched. 4 tests including the real device shape (surviving item, STOPPED, 16384 offset); the pre-existing SomethingPlaying test corrected to a genuine PLAYING context. Mutation verified. Review below-threshold items (shared IsActivelyPlaying helper, FINISHED/null-context shapes, test dedupe) landed as JF-481; the FINISHED-elicits-instead-of-seeds question is explicitly open there for a device observation.

Device test for Paolo: pause a track, wait a few seconds, 'suona jazz' -> the station question (not radio mode); then with a track actively playing, 'riproduci radio' -> radio mode unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
