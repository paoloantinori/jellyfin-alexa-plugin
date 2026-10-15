---
id: JF-889
title: >-
  JF-892: shuffle twins - null-context NRE at entry, stale-audio write path,
  cold-device empty-queue write (the JF-450/JF-655/JF-629 hardening the loop
  family has, the twins never got)
status: To Do
assignee: []
created_date: '2026-10-15 15:16'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three real defects in the shuffle twins, found by the code-review pass on the JF-890 simplify delta (2026-10-15; the review verified Alexa.NET 1.22.0's Context.AudioPlayer defaults to NULL by reflection probe against the pinned package):
1. NRE AT ENTRY (crash class): both ShuffleOnIntentHandler.cs:61 and ShuffleOffIntentHandler.cs:61 dereference context.AudioPlayer without a null guard; a request with NO AudioPlayer object (the exact JF-450 shape the loop family hardened: test HandleAsync_NoAudioContext_ReturnsNoMediaPlayingInsteadOfThrowing uses a null-AudioPlayer context) hits requestState.Token -> NRE -> the pipeline's generic catch -> the generic error line. No shuffle test constructs a null-AudioPlayer context today. Fix: nullable read + ?., mirroring ApplyRepeatModeAsync (ProgressReporter.cs:862-864).
2. STALE AUDIO TOKEN still fires the full write path: the JF-890 bound covers only the VideoApp refusal leg; on an idle device after music (frozen song token, playerActivity=STOPPED) the classifier answers Audio, the gate returns null, and the twins run ReportPlaybackProgress (reporting the dead song as progressing), ShuffleRemaining/RestoreOrder, MirrorQueueToSession keyed on the stale id, and confirm 'ShuffleEnabled'. Same Audio-verdict conflation JF-655 fixed for re-launches via PlaybackLaunchBuilder.IsAudioPlaybackActive (:727), never consulted here; the loop twin shares the tail via the idle guard's token leg.
3. COLD DEVICE (no token at all) still writes: loop answers NoMediaPlaying (JF-629 idle guard) but the twins write PlaybackOrder=Shuffle, create/persist an EMPTY device queue (DeviceQueueManager.cs:1930-1936), ShuffleOff also RestoreOrder, and speak the confirmation over nothing. Fix shape: the same HasCurrentPlaybackEvidence guard the loop family uses (or a documented deliberate preset-the-flag choice, but the loop-twin parity argues for the guard).
All three live in the same two files; one worker, one pass. Pins: null-context -> NoMediaPlaying never NRE (mirror the JF-450 loop test), stale-audio -> no write/no progress report, cold-device -> NoMediaPlaying no empty-queue persistence, active music unchanged. NOTE: JF-891 (SetPlaybackSpeed) is in flight in its own worktree on different files; no conflict.
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
