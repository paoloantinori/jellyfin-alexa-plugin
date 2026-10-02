---
id: JF-681
title: >-
  JF-681 - test-infrastructure residuals of the own-live prewrite pins:
  mid-registration split uncovered, attribution seam, prewrite read count,
  park-assert fault observation
status: In Progress
assignee: []
created_date: '2026-09-30 09:27'
labels:
  - encode-gate
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-680 -
    JF-680-the-in-lock-own-live-prewrite-row-is-unpinned-episode-song-a-miswired-gate-silently-reverts-concurrent-lock-waiters-to-live-edge-serve.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-680 orchestrator gate-marker round (findings 1, 3, 4, 5; all low-severity test-infrastructure or coverage residuals, none a defect today - the pins were verified correct on both scrutiny questions).

THE FOUR ITEMS (one task: they share the seam family and touch the same test file):
1. MID-REGISTRATION SPLIT UNCOVERED: the strict-vs-conservative gate split (OwnTicksGenerationLive at the four prewrite serve gates vs OwnTicksGenerationLiveOrRegistering at the verdict) has ZERO mechanical coverage: SetEncodeActiveForTest always creates a full slot where the two predicates agree, so a regression swapping them (or hoisting the song gate behind the verdict, the exact move the production docs warn against) keeps the suite green. Needs a zero-slot seam variant (mark the entry stored with NO slot written = the mid-registration state) + a pin asserting the serve gate stays strict (skips the prewrite) while the verdict stays conservative.
2. ATTRIBUTION SEAM: the in-lock-vs-fast-path discrimination in the JF-677/JF-680 pins rests only on the 400ms park assert; a deterministic discriminator needs a production lock-probe seam or a site-specific log line (the JF-680 task notes name this). Decide the cheapest honest shape and land it.
3. PREWRITE-ROW READ COUNT: neither JF-680 pin counts playlist reads (PlaylistContentReadForTest exists); a double-read regression on the own-live prewrite serve stays green. Add the count assertion to both pins (or a shared assertion in the helper).
4. PARK-ASSERT FAULT OBSERVATION (pre-existing JF-677 helper, inherited): the shared ServeInLockWarmCacheAsync park assert conflates a faulted endpoint task with a fast-path hit and the real exception vanishes unobserved; observe/unwrap the endpoint task's exception on assert failure so triage sees the true cause.

VERIFICATION: the new pins + red proofs per item 1 (predicate swap flips them); items 2-4 improve diagnostics/attribution without behavior change; the full existing roster (JF-675 twins, JF-677 4+4, JF-680 twins, W3 vanish pins) stays green both TFMs; NO production behavior change (a seam addition is allowed if it is test-only, null in production, like FfmpegProcessStartedForTest).
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
