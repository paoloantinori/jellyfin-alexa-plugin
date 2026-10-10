---
id: JF-782
title: >-
  JF-782 - the JF-775 dir-authority residuals: the probe-vs-verdict liveness
  straddle (a mark landing between the two reads serves the cache-root shadow
  the deleted JF-774 F3 redirect used to cover), the foreign-ticks
  mid-registration sub-window, and the full-slot foreign-registration
  overwrite (pre-existing JF-774 containment boundary)
status: Done
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - cache
  - tech-debt
dependencies:
  - JF-775
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
GUARD (2026-10-10, JF-853): any backlog CLI/MCP edit on this file FAILS with ENAMETOOLONG (regenerated slug exceeds 255 bytes) AND DELETES the file outright (pre-existing upstream Backlog.md bug, CLI v1.44.0). Hand-edit only, never MCP-edit.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent/handler; controller-internal timing work, pinned at unit level)
- [x] #8 Locale response strings added to all 17 locales (N/A: no response strings touched)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-05: legs 1 and 2 from the JF-775 /simplify round (the altitude
angle's finding 2, with the efficiency angle's F1 caveat converging on the
same seam), leg 3 from the JF-775 /code-review high round (its finding 2,
which surfaced a PRE-EXISTING gap rather than a regression); filed per the
same-turn landing rule rather than silently shipping the narrowed or
mis-documented protection.

JF-775 consolidated the dir authority into the liveness-aware probe: the
ordered probe consumes the caller's OwnTicksGenerationLiveOrRegistering read
and returns ONLY the registered dir's file while that read is true and the
registration is contained, which closed the JF-774 gate-marker addendum's
mid-registration residual. The consolidation moved the authority decision to
PROBE time, and two timing residuals now remain open:

1. THE PROBE-VS-VERDICT STRADDLE. The probe reads liveness at probe time; the
   paired debris verdict re-reads at verdict time (after the probe's file
   I/O, a microsecond-to-millisecond gap). A generation MARKED in that gap is
   unseen by the probe (which resolved the static order, first hit possibly
   the undeletable cache-root shadow) but accepted unread by the verdict (its
   loose read now true, Content null), and the serve row serves the verdict's
   first-hit file: the shadow's stale bytes. The deleted JF-774 F3 redirect
   re-derived authority at SERVE time and covered exactly this straddle on
   the own-live row (it would serve the registered dir's stream.m3u8, absent
   pre-ffmpeg, whose read vanishes and falls the request through to the
   lock-scope concurrent-serve hold). Conjunction: the undeletable same-key
   cache-root shadow + an oversize (transient-root) encode whose mark lands
   in the probe-to-verdict gap of a replay + the prewrite not yet written
   (it lands after process start). Narrower than the mid-registration window
   JF-775 closed, same blast radius when hit (the shadow's expired JF-309
   token 401s every segment fetch for the whole encode window).

2. THE FOREIGN-TICKS MID-REGISTRATION SUB-WINDOW. In the zero-slot window the
   incoming registration may be for FOREIGN ticks (an art change mid-flight):
   the resolver's containment correctly ignores the foreign registration
   (static order; the shadow is probed) and the registering verdict accepts
   it unread, so the shadow serves. No resolver shape can close this (the
   t1 answer for a dead t1 generation IS the static order); only the VERDICT
   can, by narrowing its 0-slot acceptance to registrations provably for the
   caller's ticks, which is only sound if EVERY encode path registers its
   resolved dir BEFORE marking (the episode path does since JF-774's review
   F1; the song, variants, and audiobook paths still register after their
   first-segment wait, sound today only via single-rootedness, the
   precondition now documented on VideoAudioCache.ResolveHlsGenerationDirPaths).

3. THE FULL-SLOT FOREIGN-REGISTRATION OVERWRITE (pre-existing, surfaced by
   the JF-775 code-review round). The per-key registration slot
   (_hlsDirLookup) is shared by every generation of the key, so a concurrent
   FOREIGN-ticks encode's RegisterHlsDirectoryPath overwrites it while the
   caller's OWN-ticks generation is still fully live: the resolver's
   containment correctly refuses the foreign dir, the static order answers,
   and an undeletable other-root shadow can win the liveness-accepted row.
   This is NOT a JF-775 regression (the deleted JF-774 F3 redirect had the
   same containment refusal by design, "a foreign-ticks registration can
   never redirect the serve"); it is filed here because the JF-775 wrapper
   doc's "never the other root's stale shadow" claim needed an honest scope
   for it, and the deep fix is the same family as legs 1 and 2: the
   registration must become generation-scoped (keyed (key, ticks), or the
   ActiveEncodeGenerations slot carrying its dir), so a foreign overwrite
   cannot mask the own generation's registered dir. Conjunction: the
   undeletable same-key shadow + an art change starting a foreign-ticks
   encode mid-watch + a replay of the original ticks.

FIX SHAPES (for whoever picks this up):
- Straddle: the single-snapshot shape is the deep fix: thread the probe's
  liveness answer into the verdict so ONE evaluation decides both (when the
  probe's read was false but the verdict's is true, the verdict must take the
  READ row and judge the hit's content instead of accepting unread; for the
  shadow that is the no-ENDLIST debris row, whose failed undeletable cleanup
  falls through to the transient leg). The shallow fix is un-collapsing
  (re-deriving authority at serve time), which re-adds the duplication JF-775
  removed; prefer the snapshot.
- Foreign-ticks window: adopt register-before-mark on the remaining three
  encode paths (the one-wrapper idiom), then narrow the verdict's 0-slot arm
  by registration containment.
- Foreign-overwrite: generation-scope the registration (a (key, ticks)
  lookup, or the live slot carrying its dir), keeping the tick-blind
  FindHlsDirectory leg on the per-key map it needs for post-restart segment
  resolution.
- DISCRIMINATING PIN PREREQUISITE: legs 1 and 2 need a seam that fires
  between the probe's liveness read and the verdict's (the existing
  PlaylistContentReadForTest observer fires after both), e.g. a
  ProbeLivenessReadForTest hook on the GetCachedHlsPlaylistLiveAwareAsync
  wrapper, or the verdict-threading parameter itself once shaped; leg 3 needs
  no new seam (a planted foreign registration plus the shadow plants the
  state directly).

VERIFICATION BAR: one pin per leg, red on the pre-fix tree (the straddle pin
plants the mark inside the gap via the seam; the foreign-ticks pin plants a
foreign-ticks registering entry plus the shadow; the overwrite pin plants a
foreign registration over a live own-ticks slot plus the shadow), all
asserting the shadow's marker is absent from the served bytes.

DESIGN RECORD (implementation, 2026-10-05):

- LEG 1 (straddle), the single-snapshot shape: the wrapper
  GetCachedHlsPlaylistLiveAwareAsync returns its FileInfo WITH the liveness
  answer it computed (HlsCacheProbe), and every verdict pairing threads that
  answer into ValidateHlsCacheAsync, whose own-live acceptance becomes the
  CONJUNCTION probe-answer AND fresh-read. The only cell that changes is
  probe=false/fresh=true (the mark landed in the gap): the verdict now READS
  the hit and judges it (ENDLIST serves; no-ENDLIST nulls) instead of
  accepting the static order's first hit unread. The DEBRIS CLEANUP in that
  cell does NOT fire: the JF-676 delete gate stays exactly "the one-gate
  read says not live-or-registering", so a verdict never deletes while the
  entry reports its own generation live-or-registering (the just-marked
  encode's target dir is protected the same way the own-live row protects
  it). The seam is ProbeLivenessReadForTest (an instance hook on the
  controller firing between the wrapper's liveness read and the probe's
  consumption, mirroring PlaylistContentReadForTest); the pin plants
  SetEncodeActiveForTest inside the hook's first firing.
- LEG 2 (foreign-ticks mid-registration), the verdict-side narrowing with
  the tri-state: the verdict reads the registry ONCE through a tri-state
  (Live / Registering / NotLive; one gate read, same anti-race discipline
  as IsTickLiveOrRegistering) and the Registering arm accepts only when the
  PER-KEY registration is NOT provably foreign for the caller's ticks (a
  new I/O-free cache helper: registration present AND not one of the
  caller's (key, ticks) generation dirs). An ABSENT registration keeps the
  conservative acceptance (the JF-681 twins' zero-slot state, and
  production cannot open the window without a registration once
  register-before-mark holds everywhere). Precondition adopted:
  register-before-mark moved onto the song, variants, and audiobook paths
  (the episode path already had it), so in the zero-slot window the per-key
  registration always names the marking encode's resolved dir.
- LEG 3 (foreign full-slot overwrite), the generation-scoped registration:
  RegisterHlsDirectoryPath gains the art ticks and writes a SECOND map keyed
  (key, ticks); the resolver's exclusive arm reads the generation-scoped
  entry (a foreign-ticks encode writes a different generation key and can
  no longer displace the own generation's entry), keeping the containment
  loop as defense. The per-key map is unchanged and keeps feeding the
  tick-blind FindHlsDirectory (post-restart segment resolution) plus the
  leg-2 discriminator.
- The prewrite probe's caller-gated literal (the LEG-0 GUARD NOTE below) is
  untouched; the leg-1 pin this filing demands now exists, so the guard
  note's "until it lands" condition is satisfied.

VERIFICATION RECORD (implementation, 2026-10-05):

- RED PROOFS on the seam-only tree, BOTH TFMs, all three pins failing with
  the shadow's bytes served: leg 1 ("shadow marker seg_7777 present: True",
  the strict prewrite gate's fall-through serving the shadow unread), leg 2
  (seg_7777 found in the served listing), leg 3 (seg_7777 served off the
  liveness-accepted row). Green post-fix, 3/3 both TFMs.
- Suites: 5296/5296 BOTH TFMs on the final state (the JF-775-tip lineage's
  5289 baseline + 7 new cases: 3 controller pins and 4 unit cases, the
  containment Theory counting twice); Release --no-restore -warnaserror
  clean. One one-off MonitorHls-class Dispose-backstop flake appeared in a
  mid-development class rerun (net9.0 only, passed in isolation on both
  TFMs, green in both full suites): the tracked JF-772/JF-731
  load-dependent teardown family per the JF-775 flake attribution, not a
  JF-782 regression. This worktree is the JF-775-tip lineage (the JF-777
  merge is NOT an ancestor), hence 5296 vs the JF-777 lineage's 5297.
- GATES. /simplify (4 parallel angles: reuse, simplification, efficiency,
  altitude): 6 applied (the ONE IsOwnGenerationDirPath containment
  predicate replacing the resolver/discriminator twin loops, the
  IsTickLiveOrRegistering bool collapsed onto the ReadTickLiveness
  tri-state, the foreign-evidence read folded into the acceptance
  short-circuit with the cache-side doc's read-order claim corrected to
  the actual after-the-gate order, the RegisterAndMarkEncodeActive
  one-wrapper the filing's own fix shape prescribed, the two stale
  SAME-PREDICATE doc claims reworded to the probe/verdict split account,
  and the PlantForeign(Generation|Ticks)Registration test planters);
  1 reasoned skip (the registered-dir guard-idiom extraction: the two
  readers are different maps by design, the leg-3 split itself, and the
  duplication is 3 lines of guard idiom, not a drift-prone semantic
  predicate). /code-review high: 8 finder angles, 5 findings, 4 applied
  (F1 the transient leg threads FALSE so an unread acceptance can never
  ride a liveness the resolving probe never saw; F2 resolvedHlsDir made a
  REQUIRED parameter, the JF-686 no-default shape, killing the
  silent-cache-root-registration drift a future two-root path could make;
  F3 the _hlsDirLookup field doc's false "populated when generation
  completes, cleaned up on eviction" rewritten to the encode-start,
  never-removed truth; F5 the endlistDebrisReason arm's log split so the
  judged-but-not-deleted cell is distinguishable from the cleaned one in
  the triage logs), 1 FILED as JF-783 (the generation map's one-entry-per-
  generation unbounded growth; removal belongs with the eviction sweep's
  integration, out of this serve-path scope, growth bound documented on
  the field). The review's own verdict on the three legs: each lands at
  the depth the filing prescribes.
- Production: not deployed (worker branch only); the pins are the
  regression guard for Paolo's device round.

LEG-0 GUARD NOTE (the JF-775 gate-marker's F1): the prewrite probe's caller-gated literal `ownGenerationLiveOrRegistering: true` (VideoAudioController ~:1668) is enforced only by its comment; a future cleanup replacing it with a fresh liveness read (the locally more obvious shape) re-opens the shadow window without any pin failing. The discriminating pin this filing demands for legs 1/2 ALSO covers this shape (it pins the probe-vs-verdict straddle); until it lands, treat the literal as load-bearing and any change there as needing the pin first.

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker 6b118b23, --no-ff; gate-marker seven axes PASS, red proofs reproduced, the new suites re-run in the review tree; five findings: F1 the transient fallback threads a FALSE probe answer, F2 the resolvedHlsDir required param, F3 the stale field doc, F5 the debris-hook log split, one filed as JF-783 the generation map growth), combined-tree suite verified, deployed (md5 2d611416 active). NOTE: the MCP status-flip hit the ENAMETOOLONG slug limit (the JF-724/JF-774 class) and the file was briefly lost; restored from the merge tree and closed by hand keeping the original filename.
<!-- SECTION:NOTES:END -->
