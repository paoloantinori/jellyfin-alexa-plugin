---
id: JF-792
title: >-
  JF-792 - VideoAudioControllerTests regrew to roughly two thirds of suite wall
  clock after the windowing waves; re-evaluate the declined partition lever
status: To Do
assignee: []
created_date: '2026-10-06'
labels:
  - test-infrastructure
  - performance
dependencies:
  - JF-730
priority: medium
---

## Description

Filed 2026-10-06 by the JF-730 re-measurement worker (certification pass, no
code changes; the full dated table lives in the re-measurement appendix of the
JF-730 task file). The JF-730 close declined lever (c), the
split-by-static-ownership partition, on the premise "post-fix the class is 42s
of a ~2min suite ... a new race surface for no measurable need". That premise
no longer holds.

### Tonight's attribution (bird, Debug, tree at 71feb0ba, TRX per-test durations)

- The class is 299 tests measuring 74.1s of a 114.1s summed suite on the clean
  net10 leg (65%) and 97.6s of 172.8s on the contended net9 leg (56%); the
  next-largest class is SmapiTokenRefresherTests at 4.0s. The class is ~25x
  the next class on both TFMs.
- Class filter: 1m16s net9 / 1m14s net10, both green 299/299, against 42s/42s
  (265 tests) at JF-730 close. Growth driver: +34 tests from the JF-778
  windowing wave and the JF-784/785 concat-scope work, and they skew slow.
- NO pathology: the slowest test is still the documented JF-665 stall pin
  (7.4s net9 / 7.1s net10 against 7.14s at close; the 5s
  HlsMonitorStallBudgetOverride verified at its source site). The cost is
  distributed: 17 tests at >= 1s (38.6s contended; gate-slot waits, monitor
  stall budgets, supersede kills), 62 tests in the 400ms to 1s band (40.2s;
  fake-ffmpeg process lifecycle waits incl. the production 500ms exit-poll
  granularity), 178 tests under 150ms (8.4s).

### Candidate fix shapes (risk order; all assertion-neutral per the JF-751/JF-760 arrange-only discipline)

1. THE PARTITION (the re-evaluation this task owns): remove the assembly-level
   `DisableTestParallelization` (PluginCollection.cs), put every static-touching
   test class into an explicit serialized collection (187 of 313 classes already
   carry `[Collection("Plugin")]`; the remaining ~126 need the static-usage
   audit: Plugin.Instance, QueueContinuationStore, RadioModeState,
   PlaybackReportOrdering, the encode registries, the cache root), then split
   VideoAudioControllerTests into families grouped by WHICH statics they touch
   (encode-gate family; cache-root family; position-tracker family; the pure
   token/playlist families) so within-group serialization is preserved while
   the groups run in parallel. Blast radius is the audit plus the risk a missed
   static races; mitigations: the JF-730 statics list above, and repeated
   full-suite runs (>= 5 per TFM) as the flake gate before merge.
2. PARK-FLOOR RIDER (small, only alongside 1 or as a standalone if 1 is
   declined again): the 18 park sites x ParkWindowMs=250ms cost 4.5s; JF-730
   measured the family green 10/10 at 150ms and 6/6 at 100ms, so lowering to
   150ms saves ~1.8s with the documented margin. Re-opens the slow-host margin
   decision for a ~2% class gain; do not take it alone.
3. DECLINED UPFRONT (recorded so the next pass does not re-derive): the
   production 500ms exit-poll granularity and the monitor stall budgets are
   production constants outside test reach, and JF-730 already declined the
   settle-budget seams with evidence (no test reaches any settle budget).

### Acceptance

- Wall-clock table before/after, both TFMs, contention caveat recorded (any
  parallel worker inflates absolute numbers; relative attribution only).
- Full suite green both TFMs, repeated (>= 5 consecutive clean runs per TFM as
  the race/flake gate if the partition is taken).
- Zero assertion changes; the JF-677/JF-680/JF-681/JF-700/JF-704 pin families
  green unchanged; the WarmingGateCoverageTests roster untouched.
