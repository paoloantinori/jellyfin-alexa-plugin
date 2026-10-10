---
id: JF-858
title: >-
  JF-789 residuals: bound the classifier's ladder (aged resolvable VideoApp
  entries still refuse at the flat gates) and refresh LastPlayedWrittenAt during
  live playback (shrinks the belt residual)
status: To Do
assignee: []
created_date: '2026-10-10 12:42'
labels:
  - playback
  - tech-debt
  - follow-up
dependencies: []
references:
  - >-
    backlog/tasks/jf-789 -
    the-ledger-recency-read-expose-LastPlayedWrittenAt-and-bound-the-resolver-tail-plus-the-JF-632-gate.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the JF-789 completion round (its two FOR-THE-ORCHESTRATOR items, same-turn rule; both also recorded in the shipped code comments on the window constant and HasCurrentPlaybackEvidence's doc).

(1) CLASSIFIER LADDER BOUNDING: JF-789 bounded the belt and the tail, but the classifier itself (ResolvePlayingMedium's ledger-only route/kind ladder) stays deliberately unbounded, so the flat gates (Pause/Next/Previous/Repeat) still refuse over AGED RESOLVABLE VideoApp entries (the new boundary pin documents the stance). Bounding the ladder needs its own per-family red proofs (the JF-785-class scope discipline: each flat-gate family's refusal-vs-no-media flip decided consciously), and should revisit whether the JF-789 window or an evidence-exists signal is the right discriminator per gate.

(2) SERVER-SIDE STAMP REFRESH: the accepted belt residual (a belt-only session longer than the 60-min window - opt-in NativeControlsForAudio seek mode, album concat past its first hour, frozen token - loses belt protection) shrinks to nothing if LastPlayedWrittenAt is refreshed during genuinely live playback. Candidate mechanisms named by the worker: the JF-655 PlaybackStarted flag, or the video-audio segment tracker (which already observes live segment requests). Mind the write-frequency tradeoff (a per-segment write vs the tracker's existing cadence) and the JF-694 write-gate discipline.
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
