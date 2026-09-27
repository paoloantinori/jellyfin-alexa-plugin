---
id: JF-655
title: >-
  JF-655 - a speed request with nothing playing restarts stale audio at the new
  rate (the medium gate trusts the persistent ledger alone); require an
  active-playback signal before the re-launch paths
status: To Do
assignee: []
created_date: '2026-09-27 13:31'
labels:
  - playback-speed
  - device-found
  - e2e-finding
dependencies: []
references:
  - backlog/tasks/jf-636*.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the full e2e battery (99 passed / 42 failed; the 40 open-error failures are the chronic class, the fast-mode flip re-verified green in isolation; THIS is the one deterministic real finding).

THE BEHAVIOR (reproduced isolated, it-IT, simulate-skill): 'a velocità uno e mezzo' with NOTHING actually playing answers 'Velocità uno e mezzo.' and re-launches the last-played track at the new rate, instead of the expected cold refusal 'Nessun contenuto in riproduzione'. Root cause: SetPlaybackSpeed's medium gate (ResolveScreenOwningMedium) reads the DEVICE QUEUE LEDGER (RecordLastPlayed), which persists long after playback stops. A stale Audio ledger entry makes the resolver say Audio -> the JF-636 speed re-launch path fires. The user's mental model is "change what is playing"; hours after music stopped, the request silently restarts old audio at a new speed (and on a REAL device, a one-shot speed request killing/restarting a stale stream is the JF-635 class of surprise).

FIX DIRECTION: the speed (and the loop-family and sleep-timer gates that share ResolveScreenOwningMedium) should require an ACTIVE playback signal, not just a ledger entry: corroborate with a live signal (a recently-active playstate: the plugin's own PlaybackStarted/Stopped tracking, InteractionDiagnostics.SincePlaybackStarted, or a /Sessions playstate check bounded to the device) before the re-launch/apply paths; a stale ledger alone yields the honest refusal (the JF-632/JF-564 refusal shape). Watch the JF-639 standing-rate threading and the F1 residual-seed path (TvNextUp) which also rely on the medium gate: the active-signal requirement must not break the genuinely-playing cases (pinned tests: SetPlaybackSpeed tests, Loop family, SleepTimer arming).

VERIFICATION BAR: the e2e row passes cold (nothing playing -> 'Nessun contenuto in riproduzione'); a playback-then-speed sequence still applies the rate (the JF-636 behavior unchanged when audio is genuinely active); the suite green; the e2e fast/full chains for it-IT re-run clean.
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
