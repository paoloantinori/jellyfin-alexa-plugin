---
id: JF-632
title: >-
  Sleep-timer re-issue over VideoApp-routed media must refuse honestly instead
  of minting an AudioPlayer.Play of the stale audio item
status: Done
assignee: []
created_date: '2026-09-25 03:04'
updated_date: '2026-09-25 07:13'
labels:
  - bug
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SleepTimerIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PauseIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/SleepTimerIntentHandlerTests.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-628 code-review (2026-09-25, K1 finding; the ledger half was fixed in JF-628 itself, this task owns the PRE-EXISTING directive half).

SleepTimerIntentHandler mints a ReplaceAll AudioPlayer.Play re-issue of whatever context.AudioPlayer.Token / session.FullNowPlayingItem names, with NO playing-medium gate. When a VideoApp-launched medium is on screen (movie, episode, live TV, NativeControlsForBooks audiobook), the VideoApp launch never touched context.AudioPlayer.Token, so the handler resolves the STALE audio token/session and issues an AudioPlayer.Play for the stale item over the running video: parallel audio the platform cannot stop over the player, or a dead re-issue. JF-628 closed the ledger half (the RecordLastPlayed write skips when the ledger holds a VideoApp-routed record for a different item), but the directive still goes out. Proper fix shape: gate the whole re-issue on the playing medium being audio-routed (ResolvePlayingMedium / the ledger snapshot route, the same evidence the JF-628 ledger gate reads) and answer with an honest tell for video media (the PauseIntentHandler JF-564 transport-refusal precedent); needs a locale string in all 17 locales. Add the case to the JF-628 sleep-timer tests (HandleAsync_ArmingOverVideoAppRoutedLedger_KeepsTheVideoAppRecord currently asserts the directive is still minted - it pins today's shape and must be re-pinned when this lands).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-632 complete: the sleep re-issue refuses honestly over ANY VideoApp-routed medium. The gate (ResolvePlayingMedium + IsVideoAppMedium, then the ledger-route check that needs no item resolution) absorbed the JF-628 belt entirely; the code review PROBED and closed two real holes (the same-item seek-mode shape that flipped the ledger route back to Audio while double-audioing the same track; the unresolvable deleted-item shape that shipped a deadline that could never fire). The refusal rides BuildPauseResponse (AudioPlayer.Stop for displaced audio, the JF-564 precedent), the line follows the on-screen shape (seek-mode vs video family), and CannotSetSleepTimerOverVideo was reworded in all 17 locales to the honest boundary after the review flagged the over-claim. Gates: /simplify (2 combined agents) + code-review high (6 findings: all applied) in the orchestrator transcript. Suite 4322x2, locales PASS, DEPLOYED; the push is parked behind the deploy-gate hook's demand for evidence on the older device-verification units (their gates can only run at the device round) - rides the next session's push.
<!-- SECTION:FINAL_SUMMARY:END -->
