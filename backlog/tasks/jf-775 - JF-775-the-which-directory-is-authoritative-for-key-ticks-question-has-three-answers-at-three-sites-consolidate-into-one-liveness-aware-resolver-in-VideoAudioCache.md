---
id: JF-775
title: >-
  JF-775 - the "which directory is authoritative for (key, ticks)" question has
  three answers at three sites; consolidate into one liveness-aware resolver in
  VideoAudioCache
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-10 12:52'
labels:
  - streaming
  - cache
  - tech-debt
dependencies:
  - JF-774
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
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
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Notes

<!-- SECTION:NOTES:BEGIN -->
DESIGN (worker, 2026-10-05; written before the implementation per the
design-first instruction).

THE RESOLVER'S CONTRACT. One method in VideoAudioCache,

  ResolveHlsGenerationDirPaths(itemId, artModifiedTicks,
  ownGenerationLiveOrRegistering) -> string[]

answering "which directory serves this key RIGHT NOW" as an ORDERED
candidate list. Decision rule: when the caller's verdict-family liveness
read is true (OwnTicksGenerationLiveOrRegistering over the path's registry,
the SAME read the debris verdict accepts on) AND the registered directory
(TryGetRegisteredHlsDirectory, stored at encode start BEFORE MarkEncodeActive
on the episode path) IS one of the (key, ticks) generation dirs, the
registered dir is returned EXCLUSIVELY: the live-or-registering encode owns
the generation and the other root's same-key file is necessarily stale
(JF-774's exclusive rule, now extended over the mid-registration window the
addendum files: the registration already names the incoming encode's dir at
exactly the moment the 0-slot verdict errs conservative). Otherwise the
static root-preference order answers (cache root first, transient second,
today's HlsGenerationDirPaths). The liveness bool is CALLER-SUPPLIED because
the registries are controller-owned; the containment test keeps a
foreign-ticks registration from ever redirecting (the JF-774 F3 rule).

WHO CALLS IT. (1) GetCachedHlsPlaylist gains the required bool and probes
the resolver's order; all eight controller call sites pass their own
registry's loose read (episode fast + in-lock, song fast + in-lock, variants
fast + in-lock, audiobook fast + in-lock). (2) The episode prewrite probe's
hand-rolled arms (the registered == transientDir exclusive arm plus the
ordered else-arm) collapse into one resolver-fed loop with the loose read.
(3) The tick-blind leg does NOT call it: FindHlsDirectory/ByScan (the
segment path) has no ticks to key on, keeps its registration-first arm
consulting the same registration read, and keeps the JF-774 cross-root
recency rule byte-identical. CleanupHlsStub also stays on the static order:
cleanup asks "which directories exist", not "which serves".

WHAT EACH OF THE THREE ANSWERS BECOMES. Answer 1 (HlsGenerationDirPaths'
static order) becomes the resolver's not-live arm, unchanged for every
not-live probe (the pin battery's resting-entry shapes are byte-identical).
Answer 2 (the scan's cross-root recency) stays as-is, cross-referenced as
the tick-blind boundary. Answer 3 (the serve rows' live-override) collapses
INTO the probe: with the probe resolver-fed, the verdict's accepted FileInfo
IS the registered dir's file on every liveness-accepted row, so the F3
registered-dir fallback block on ServeEpisodeWarmCacheAsync deletes (its
target now equals valid.Playlist.FullName) and the mid-registration
fall-through row serves the registered dir's playlist instead of the ordered
probe's first hit. The strict gate at the serve row itself is KEPT (the
JF-681 "do not hoist the gate" warning stands: the mid-registration window
still skips the prewrite; only the DIRECTORY each row reads changes).

THE CLOSED RESIDUAL (the addendum's shape): in the mid-registration window
the verdict accepts without reading, the strict gate skips prewrite and
redirect, but the probe already returned only the registered dir's file, so
the fall-through serve reads the REGISTERED dir's playlist (redirect) or
misses the probe when the encode has not written stream.m3u8 yet and holds
on the per-item lock until the first segment (the concurrent-request flow).
The cache-root shadow is never probed in the window.

DOCUMENTED DELTA: a replay inside the window when the registered dir has no
stream.m3u8 yet (before the first segment) now misses and holds on the lock
instead of serving the prewrite off the back of a shadow-hit verdict
acceptance; that replay previously could only serve at all by accepting the
stale shadow, which is the bug. The own-live no-prewrite row and both
JF-774/JF-778 pin families keep their served bytes.

RED PROOF: the pin plants the registering seam (SetEncodeRegisteringForTest,
JF-681) plus the encode-start registration (RegisterHlsDirectoryPath at the
transient dir, the production ordering) plus the undeletable cache-root
shadow (a stale no-ENDLIST stream.m3u8) plus the transient live partial; on
the current tree the ordered probe's first hit is the shadow, the
registering verdict accepts it unread, the strict gate skips both redirects,
and the fall-through serves the shadow's stale bytes (the marker). Post-fix
the probe is exclusive to the registered dir and the same row serves the
transient live partial.

GATES (worker, 2026-10-05). /simplify (4 parallel angles): APPLIED the
one-wrapper GetCachedHlsPlaylistLiveAwareAsync (the 8-site
GetCachedHlsPlaylist/OwnTicksGenerationLiveOrRegistering pairing homed once,
the LockHlsItemAsync call-sites-cannot-drift idiom; convergent
simplification+reuse finding), the PlantTwoRootShadowFixture test planter
(the two new controller pins' 35 duplicated lines extracted at the second
copy, the PlantLiveEncodeFixture convention), the cache-root containment
Theory variant (the rule-1 unit pin now covers BOTH roots' registrations),
and the comment honesty pass (the probe-time vs verdict-time evaluation
distinction made explicit in the wrapper doc; the per-site rationale copies
trimmed; the ServeEpisodeWarmCacheAsync inline comment slimmed to the
doc-unique facts). SKIPPED with reasons: threading the probe's liveness
snapshot into the verdict (the verdict's post-I/O re-read is deliberate
freshness; the residual straddle FILED as JF-782 leg 1), the raw
registration read before containment in the resolver (would fork the ONE
TryGetRegisteredHlsDirectory predicate into a second shape, the exact drift
JF-774's single-homing rule exists to prevent, to save one warm stat on
rare shapes), and Contains over the explicit Ordinal loop (the house style
spells the comparison kind). /code-review high: 4 findings; F1 APPLIED (the
prewrite probe's exclusive source selection pinned caller-gated instead of
re-reading liveness: a monitor CLEAR landing between the serve row's strict
gate and the probe's fresh read would have flipped the source to the static
order's stale cache-root leg, a real narrowing the collapse introduced; the
prewrite method now carries the caller-precondition doc), F2's doc half
APPLIED (the wrapper's "never the other root's stale shadow" claim scoped to
its honest containment+timing boundaries) and its behavioral half FILED as
JF-782 leg 3 (the full-slot foreign-registration overwrite, PRE-EXISTING
JF-774 containment design, not a JF-775 regression), F3/F4 confirmed as the
JF-782 legs 1/2 this task filed from the simplify round (no double-file).

FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

FLAKE ATTRIBUTION (the JF-775 gate-marker's F3, for future bisect/triage): two class-level reruns during development showed one-off MonitorHls-family Dispose-backstop flakes (a DIFFERENT test each run, all passing in isolation on both TFMs, the full suites eventually green with zero assertion failures of this change's own). This is the tracked JF-772/JF-731 load-dependent teardown family on this shared machine, NOT a JF-775 regression; committed here so a bisect hitting the range does not re-diagnose it.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED (worker branch, not merged): the ONE liveness-aware resolver
VideoAudioCache.ResolveHlsGenerationDirPaths(key, ticks,
ownGenerationLiveOrRegistering) unifying the three per-site dir-authority
rules JF-774 left split: while the caller's verdict-family read is true AND
the registration names one of the (key, ticks) generation dirs, that dir is
the EXCLUSIVE probe source (JF-774's exclusive rule extended over the
mid-registration window and to the cache-root containment arm); otherwise
the static root-preference order (byte-identical to the old behavior on
every not-live probe). Consumers: the ordered probe (GetCachedHlsPlaylist
gained the REQUIRED bool, all eight controller sites fed through the ONE
GetCachedHlsPlaylistLiveAwareAsync wrapper that pairs the cache probe with
each path's registry read) and the episode prewrite probe (its hand-rolled
registered==transientDir arm collapsed into the resolver, the liveness
answer pinned caller-gated per code-review F1); the JF-774 F3 serve-row
redirect block is DELETED (the probe already returns the registered dir's
file on every liveness-accepted row), and the tick-blind scan leg keeps the
JF-774 cross-root recency intact with the boundary documented. THE CLOSED
RESIDUAL (the gate-marker addendum): in the mid-registration window the
registering verdict accepts without reading, but the probe never reaches the
cache-root shadow, so the fall-through serves the registered dir's live
partial (redirect) or misses and holds on the per-item lock until the first
segment (the concurrent-request flow). RED PROOF on the unmodified tree,
both TFMs: the new pin failed with the predicted mode (the shadow's stale
bytes served); post-fix green. TESTS: 2 controller pins (the mid-registration
red proof; the F3-collapse guard, sharing the PlantTwoRootShadowFixture
planter) + 3 resolver unit pins (rule 1 as a 2-case Theory over both roots'
registrations, rule 2 the foreign-generation containment refusal, rule 3 the
not-live static order) + 6 existing probe call sites migrated to the new
required parameter. GATES: /simplify (4 parallel angles; 4 applied incl.
the one-wrapper and the test planter; 3 reasoned skips) + /code-review high
(4 findings: F1 the prewrite clear-race APPLIED, F2's doc scope APPLIED with
its pre-existing behavioral half FILED as JF-782 leg 3, F3/F4 the JF-782
legs 1/2 this task filed from the simplify round). SUITES: 5289/5289 BOTH
TFMs, full run on the final state; Release --no-restore -warnaserror clean.
RESIDUALS FILED: JF-782 (the probe-vs-verdict straddle, the foreign-ticks
mid-registration sub-window, the full-slot foreign-registration overwrite).
NOT DEPLOYED (worker branch only; no merge into main).

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker d9038667 + orchestrator tail 6560c2cd, --no-ff; the gate-marker's six axes PASS with the collapse grep-verified and the F1 caller-gated pin read at source; its four findings dispositioned in the tail), suites 5289/5289 both TFMs. Deploys batched with JF-777. JF-782 filed by this task.
<!-- SECTION:FINAL_SUMMARY:END -->
