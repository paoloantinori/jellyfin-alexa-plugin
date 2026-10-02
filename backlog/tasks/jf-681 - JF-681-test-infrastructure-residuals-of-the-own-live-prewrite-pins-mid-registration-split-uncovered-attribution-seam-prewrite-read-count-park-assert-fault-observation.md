---
id: JF-681
title: >-
  JF-681 - test-infrastructure residuals of the own-live prewrite pins:
  mid-registration split uncovered, attribution seam, prewrite read count,
  park-assert fault observation
status: To Do
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
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-infrastructure only, no new intent or handler logic)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes
<!-- SECTION:IMPL:BEGIN -->
Landed in the worker worktree (single commit, not pushed), baseline 7bc89eb1, suite 4891/4891 both TFMs (4887 + 4: the two mid-registration pins, the park-fault observation pin, the throwing-probe lock-release pin).

ITEM 1 (mid-registration split): new seam `SetEncodeRegisteringForTest` (stores a ZERO-slot holder via the indexer set, mirroring MarkActive's GetOrAdd-before-RegisterIfStored window; clear arm delegates to SetEncodeActiveForTest(active:false); test-only, production never calls it) + pins `StreamHlsEpisode_InLockMidRegistrationWindow_SkipsPrewriteAndVerdictStaysConservative` and the song twin: in the window the strict gate skips the prewrite (seg_0999/seg_899 absent, zero prewrite reads, no prewrite log) while the verdict answers conservative (the "serving live ffmpeg playlist (encoding in progress)" own-live row fires, the discriminator an ABSENT entry cannot produce) and the live partial serves. RED PROOF: swapping both gates to OwnTicksGenerationLiveOrRegistering flips both pins (prewrite served, markers present).

ITEM 2 (attribution seam): `InLockWarmCacheProbeForTest` (null-inert delegate) fired at the ONE `LockHlsItemAsync` wrapper that all four HLS in-lock scopes route through (MP4 stays direct), and `ServeInLockWarmCacheAsync` gained an optional `VideoAudioController? controller` param: when passed, the helper wires the seam and asserts the probe fired after the endpoint completes (the 400ms park assert can no longer be satisfied by a fast-path serve). EIGHT pins opted in (4 JF-677 twins, 2 JF-680, 2 mid-reg). RED PROOF on the final shape: removing the wrapper's invoke flips exactly those 8, the 9 other helper callers stay green. The JF-678 vanish/breach family (no controller passed) is filed same-turn at JF-700.

ITEM 3 (prewrite read count): the two JF-680 own-live pins count reads through the shared `TrackPlaylistReads` funnel (prewrite==1 fresh serve read, live==0). RED PROOF: a discarded ReadPlaylistContentAsync hoisted into both TryServePrewritten* helpers flips both pins to 2.

ITEM 4 (park-assert fault observation): the helper's park assert routes through `ParkAssertFailureMessage`, which unwraps a FAULTED endpoint task (Exception.GetBaseException, type+message+stack into the assert message) and names CANCELED, keeping the original fast-path wording verbatim for a real fast hit. Pin `ServeInLockWarmCacheHelper_FaultedEndpointTask_SurfacesExceptionThroughParkAssert` (verified Xunit 2.7 throws FalseException, not FailException). RED PROOF: reverting the builder to the old constant drops the exception text and the pin fails.

GATES: simplify (4 agents; applied the helper-param probe shape, TrackPlaylistReads, the total GetBaseException unwrap, the LockHlsItemAsync one-definition wrapper, the clear-arm delegation; skipped justified: TestHelpers.CreateSong migration (file precedent is 45 inline constructions), double Snapshot calls (file-wide convention, micro)). Code-review high: finding 1 (throwing observer orphans the acquired gate) FIXED with the wrapper's dispose-and-rethrow catch + pin `StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock` (RED PROOF: removing the catch times out the follow-up acquisition at 15s, both TFMs); finding 2 = the JF-678-family asymmetry, already tracked as JF-700 (not re-filed). Full suite on the final state: 4891/4891 both TFMs, 0 warnings, recipe dotnet test -m:1, never --no-build. DoD 4-8 N/A (test-infra). No deploy; do not push.
<!-- SECTION:IMPL:END -->
