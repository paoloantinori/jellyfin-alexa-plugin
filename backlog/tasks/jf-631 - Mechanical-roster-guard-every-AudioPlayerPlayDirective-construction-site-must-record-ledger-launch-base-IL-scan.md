---
id: JF-631
title: >-
  Mechanical roster guard: every AudioPlayerPlayDirective construction site must
  record ledger + launch base (IL scan)
status: In Progress
assignee: []
created_date: '2026-09-25 03:04'
updated_date: '2026-09-25 11:02'
labels:
  - tech-debt
  - tests
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SleepTimerIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-628 code-review (2026-09-25, finding 4, same-turn filing rule).

The invariant "every AudioPlayer.Play launch site records the device ledger (RecordLastPlayed) and its launch base (RecordLaunchBase)" is enforced only by prose: the doc enumeration in PlaybackLaunchBuilder.ResolvePlayingMedium (~line 343). Production has exactly three AudioPlayerPlayDirective construction sites: the BuildAudioPlayerResponse chokepoint (records both) and the two in SleepTimerIntentHandler's re-issue paths (arm + cancel, fed by the single JF-628 write site). This exact hand-assembly has already produced two sequential misses at the sleep site (JF-522 forgot the base write, JF-628 forgot the ledger write); the next off-chokepoint site (a future rewind, replay, or queue-repair handler minting its own AudioPlayer.Play) will miss a write the same way and surface only as on-device resume/classification desync.

Ask: extend the existing IL-scanning machinery (IlCallScanner, which already feeds WarmingGateCoverageTests and SessionQueueReaderRosterTests) with newobj/constructor-call detection and add a roster test asserting every method that constructs an AudioPlayerPlayDirective is either the chokepoint or calls both RecordLastPlayed and RecordLaunchBase (with the VideoApp-displacement exception the JF-628 gate documents). Fails loudly at test time instead of drifting silently. NOTE: a combined RecordLaunch(both writes) helper was reviewed and REJECTED for the chokepoint (its two writes are deliberately non-adjacent, split by the native-controls delegation gate: RecordLastPlayed before it with route Audio, RecordLaunchBase after it and skipped on the VideoApp delegation), so the roster test is the enforcement layer, not a shared method.
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
