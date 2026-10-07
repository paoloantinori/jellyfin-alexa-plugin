---
id: JF-772
title: >-
  JF-731 Dispose-backstop full-suite flake on net10.0: a temp-dir process
  observed at a successor test's teardown (base-reproducible, load-dependent)
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - tests
  - flake
  - videoaudio
dependencies: []
references:
  - JF-731
  - JF-730
  - JF-537.1
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed same-turn from the JF-537.1 cycle (2026-10-05), per the every-finding-lands
rule: during JF-537.1's full-suite verification the JF-731 Dispose backstop
assert fired on net10.0 in 2 of 3 runs (`MonitorHls_LatePriorGenerationClear_KeepsNewerEncodeFlagSet`
both times: "1 temp-dir process kill target(s), gate refilled to its configured
cap"). CAUSALITY CHECK RAN BOTH WAYS: two full-suite runs on the UNMODIFIED base
(682c6409, JF-537.1's changes fully reverted) reproduced the SAME backstop
failure class on net10.0 (run 1 green/green, run 2 green on net9.0 + FAILED on
net10.0 at a DIFFERENT test, `StreamHlsEpisode_RemuxTier_ReadsMediaStreamsOnce`,
identical message shape). So the flake is PRE-EXISTING and environmental, not a
JF-537.1 regression; JF-537.1's ~10 added class tests raise class runtime and
plausibly the flake's probability (2/3 vs 1/2 observed), which only shifts WHICH
successor test observes the leftover.

Signature: exactly one "/proc temp-dir process kill target" at a test's Dispose,
zero registry and pid-file targets, and the gate refills right after the kill.
That matches the backstop's own documented corner ("an encode born after a
PRIOR test's sweep completed lands here") plus kill-delivery latency: the
observed process is a fake-ffmpeg script (or its sleep child) whose kill signal
was in flight while the OWNING test's Dispose sweep had already completed, so
the NEXT test's teardown observes and counts it. Candidates to investigate:
(a) kill-in-flight at teardown (the fence idiom exists, `FenceTempDirEncodesDeadAsync`,
but not every encode-driving test's finally carries it; JF-537.1 added the
missing one to `MonitorHls_LatePriorGenerationClear_KeepsNewerEncodeFlagSet`,
which only hardens that single site); (b) the gate's 500ms exit-poll release
window vs the Dispose drain's 2s no-kill budget under two-TFM parallel load;
(c) a systematically slower net10.0 testhost. Reproduction needs FULL-SUITE
runs (class-filtered runs were consistently green, 5+ attempts), so any fix
needs the two-run base-vs-fixed comparison this filing used.
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

JF-808 GATE-MARKER ADDENDUM (2026-10-07): a NEW DATA POINT for this flake - the JF-731 dispose-backstop fired once on the FIRST full net9.0 run of the JF-808 tree (matrix: isolation 174/174 green, immediate re-run green, the net10.0 leg green, the base 1ea40cd4 tree green 5458/5458; the JF-808 worker initially filed it as JF-810 before the gate-marker caught the duplicate). Relevance: this filing's candidate mechanisms named the net10.0 testhost being systematically slower; the net9.0 occurrence weakens the TFM-specific reading and strengthens the plain load-window reading (the parallel phase's 8-lane starvation class). The JF-810 filing is CLOSED as this task's duplicate; all evidence lives here.

JF-808 GATE RUN (2026-10-07): a second one-shot net9.0 failure in the JF-808 merged-tree full gate (name lost to scroll-off; the immediate re-run fully green 5466/5466 both TFMs). Same signature as the JF-808-tree occurrence: net9.0-only, single-shot, green on re-run. Two net9.0 data points in one day against the net10.0-specific candidate; the load-window reading stands.

JF-797 GATE RUN (2026-10-07): a one-shot net10.0 failure (name lost to scroll-off; two immediate re-runs fully green 5484/5484 both TFMs; the locale mechanism remains structurally closed). Fourth data point: the flake now observed on BOTH TFMs, alternating - consistent with the plain load-window reading.
