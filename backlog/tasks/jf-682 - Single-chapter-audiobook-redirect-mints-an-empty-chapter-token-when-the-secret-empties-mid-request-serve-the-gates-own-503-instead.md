---
id: JF-682
title: >-
  JF-682 - single-chapter audiobook redirect mints an empty chapter token when
  the secret empties mid-request; serve the gate's own 503 instead
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - robustness
  - encode-gate
dependencies:
  - JF-678
references:
  - >-
    backlog/tasks/jf-678 - Token-less-serve-skips-the-JF-499-W3-vanish-probe-PhysicalFile-over-a-vanished-playlist-500s-at-result-execution.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-678 /simplify round (altitude agent finding 2), the one substantive recommendation the round did NOT apply because it changes auth behavior beyond the vanish family's scope and would orphan the no-token pin JF-678 was required to land.

THE SHAPE: `StreamHlsAudiobook`'s route gate (`ValidateStreamToken`) 503s any request when `StreamTokenSecret` is empty ("Stream token secret not configured"), but the SINGLE-CHAPTER redirect further down re-reads the secret and, finding it empty, mints an EMPTY chapter token (`string.Empty`), handing `StreamHlsVideoAudioCore` an override token that flips every serve on the song path into the no-token raw-PhysicalFile branch. The only window is a config save emptying the secret between the gate's read and the redirect's re-mint (same request, microseconds apart), which is also the only reachable entry into the no-token branch JF-678 probed.

WHY THE 503 IS BETTER: a tokenless playlist serve is dead audio either way (its segment lines carry no token, so `GetSegment` 401s every segment), but the current shape answers 200-then-per-segment-401s, which is harder to diagnose than the gate's own 503 with the explicit "secret not configured" log. After JF-678 the shape additionally kicks a full re-encode before handing out the dead playlist, so the wasted work is real.

FIX DIRECTION: at the single-chapter branch, an empty secret should return the gate's own 503 shape (one line, mirroring `ValidateStreamToken`'s decision) instead of minting an empty token. The JF-678 no-token probe stays as defense-in-depth for the branch, but with this fix the branch becomes unreachable in production; the JF-678 no-token pin (`StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode`) drives the CURRENT reachable shape via `SecretClearingLoggerProvider` and would need to be retired or reworked (drive the probe through a different construction) in the same change that lands this.
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
