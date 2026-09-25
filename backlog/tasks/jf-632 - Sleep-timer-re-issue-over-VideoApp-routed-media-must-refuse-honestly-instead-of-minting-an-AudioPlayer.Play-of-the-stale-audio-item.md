---
id: JF-632
title: >-
  Sleep-timer re-issue over VideoApp-routed media must refuse honestly instead
  of minting an AudioPlayer.Play of the stale audio item
status: To Do
assignee: []
created_date: '2026-09-25 03:04'
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
