---
id: JF-631
title: >-
  Mechanical roster guard: every AudioPlayerPlayDirective construction site must
  record ledger + launch base (IL scan)
status: Done
assignee: []
created_date: '2026-09-25 03:04'
updated_date: '2026-09-25 11:54'
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
JF-631 complete: the IL roster guard. IlCallScanner gained newobj detection (ConstructsType; AudioPlayerPlayDirective resolves as a cross-assembly MemberRef); AudioPlayerPlayConstructionRosterTests holds two facts: roster equality (bidirectional, so neither an empty nor an extra discovery passes) and the writes invariant (every discovered site calls BOTH RecordLastPlayed and RecordLaunchBase, presence-level, one-level same-type helper allowance, aggregated failure message). The implementer PROVED the negative control (commenting the write fails both TFMs with the what-to-do message, restored byte-identical). The review hardened two probe-confirmed escapes: derived directives (IsAssignableFrom now, a subclass still serializes as AudioPlayer.Play) and double-nested compiler names (async lambda/local-function state machines); the docs now state the newobj-only boundary and the presence-level limit honestly. The simplify pass folded the scanner walk (OperandTokens) and hoisted TryResolveMethod (was in its third copy); JF-634 filed same-turn for the remaining scaffolding hoist. Test-only: no production changes, no deploy needed. Gates both in the orchestrator transcript. Suite 4334x2, PUSHED.
<!-- SECTION:FINAL_SUMMARY:END -->
