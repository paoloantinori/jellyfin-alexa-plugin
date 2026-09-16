---
id: JF-582
title: >-
  APL next/prev parity with the intent handlers: apply the JF-507 codec gate and
  JF-564 medium refusal, and extract the shared adjacent-queue-item serve (~60
  duplicated lines across 4 sites)
status: To Do
assignee: []
created_date: '2026-09-16 21:22'
labels:
  - reliability
  - refactor
  - queue
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-579 gates (2026-09-16). Code-review minor (pre-existing, widened by the rehydration adoption): AplUserEventHandler.HandleNext/HandlePrevious launch adjacent queue items via Launch.GetStreamUrl directly, bypassing BOTH the JF-507 codec gate (Launch.ResolveAudioLaunchSource: an EAC3-family video item as queue successor routes to the audio-only transcode instead of dying on raw static bytes) and the JF-564 VideoApp-medium refusal that NextIntentHandler/PreviousIntentHandler apply. Reach is narrow (the APL NowPlaying screen implies audio was the most recent play) but a cross-media queue with a video successor plus an APL next tap now executes the launch path on rehydrated content and would serve an unplayable stream. /simplify S1+S2: the guard-plus-resolve pair is now pasted at four sites (the rehydrated local is dead except as the resolve argument; a combined TryRehydrateAndResolveCurrentItemId helper would couple them), and the four adjacent-item bodies (Next/Previous intent handlers + APL HandleNext/HandlePrevious) became shape-identical at entry with three drift dimensions already present (no medium refusal, no codec gate, no logging on the APL side); a shared ServeAdjacentQueueItem(session, user, context, direction) on ProgressReporter would delete ~60 duplicated lines and remove the drift surface. Scope when picked: extract the shared adjacent-item serve WITH the JF-507 gate and JF-564 refusal applied uniformly to all four call sites, red-first tests for the APL video-successor case (transcode route instead of raw static), and consider the combined rehydrate-and-resolve helper. Also accepted residue from the JF-579 /simplify pass (T1): SessionQueueReaderRosterTests duplicates ~30 lines of the WarmingGateCoverageTests IL-scanner (ContainsCallToToken vs ContainsGateCall, DeclaredCallableMethods); a shared parameterized scanner in the test project would dedupe, rides here since it touches the same test family. Related: JF-574, JF-577, JF-579, JF-507, JF-564.
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
