---
id: JF-775
title: >-
  JF-775 - the "which directory is authoritative for (key, ticks)" question has
  three answers at three sites; consolidate into one liveness-aware resolver in
  VideoAudioCache
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - cache
  - tech-debt
dependencies:
  - JF-774
priority: low
---

## Description

Filed 2026-10-05 from the JF-774 high code-review round (its finding 4, altitude
class; dispositioned SKIP-with-reasons in that task, filed here per the
same-turn landing rule).

After JF-774, the question "which directory is authoritative for a
(key, ticks)" is answered by THREE different rules at three sites, each with
its own rationale comment and no single owner:

1. `VideoAudioCache.HlsGenerationDirPaths` (the declared ONE home of the
   root-preference order): cache root first, transient root second. Consumed
   by the playlist probe (`GetCachedHlsPlaylist`), the prewrite probe's
   no-live-encode branch, and `CleanupHlsStub`.
2. `VideoAudioCache.FindHlsDirectoryByScan` (JF-774 finding 1): newest
   generation ACROSS both roots by CreationTimeUtc, cache root winning ties
   (a scan has no caller ticks to prefer; a stale orphan must not outrank the
   current generation).
3. The episode serve rows (JF-774 findings 2/3 + review findings 1/3): while
   the caller's own generation is LIVE, the encode's REGISTERED directory
   (registered at encode start) is authoritative: the prewrite probe serves
   ONLY the registered transient dir when it owns the generation, and the
   own-live warm serve falls back to the registered dir's stream.m3u8 over
   the verdict's first-hit file.

The runtime windows the review could name (the mark-to-registration gap, the
stale-registration regression, the in-leg masking, the own-live stale-bytes
fallback) were all CLOSED by the applied JF-774 fixes, so this is a
maintenance-shape finding, not a live defect: the residual cost is that each
new call site of `GetCachedHlsPlaylist`/`FindHlsDirectory`/the prewrite probe
must re-derive which rule applies, and a future consumer (the variants core
gaining transient mode, a third root) can silently pick whichever ordering it
happens to reach.

THE CONSOLIDATION (the reviewer's shape): one liveness-aware resolver in
`VideoAudioCache` that takes the active encode's resolved directory into
account (the registration, or the registry once it carries the dir) and
returns the authoritative dir for (key, ticks), consumed by the ordered
probe, the scan fallback, and the prewrite probe alike; the three per-site
rules collapse into it.

WHY IT WAS NOT DONE IN JF-774 (the recorded skip): the serve rows it would
rewrite are the JF-677/JF-678-pinned rows whose closed legs JF-537.1
deliberately kept byte-identical (the in-lock pins discriminate serving
branches by exact log lines), and the repo's own JF-650 re-arm discipline
defers consolidation until a second qualifying instance appears (a third root
or a second path gaining transient mode is that trigger). Do it as its own
task with the pin battery re-baselined deliberately, not as a rider on an
edge-fix task.

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

GATE-MARKER ADDENDUM (2026-10-05, from the JF-774 orchestrator review): the
mid-registration window is NARROWED, not closed; a lock-free fast-path replay
landing between the registry store and the first slot write inside
MarkEncodeActive reads a registering verdict that accepts the cache-root
shadow without reading (OwnTicksGenerationLiveOrRegistering), while the
prewrite override and the registered-dir fallback both sit in the strict
OwnTicksGenerationLive gate, so the fall-through serve at the ordered probe's
first hit serves the stale listing unredirected. The 'runtime windows were
all CLOSED' sentence above is corrected by this addendum. The window is
sub-second and requires the undeletable same-key shadow plus an oversize
encode plus a replay in the gap; the liveness-aware resolver this task tracks
is the shape that closes it.
