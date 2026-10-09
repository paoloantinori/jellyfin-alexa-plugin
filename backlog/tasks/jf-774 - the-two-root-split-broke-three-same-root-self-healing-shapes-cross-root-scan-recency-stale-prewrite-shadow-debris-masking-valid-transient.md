---
id: JF-774
title: >-
  JF-774 - the two-root split (JF-537.1) broke three same-root self-healing
  shapes: the cross-root scan ignores generation recency, a stale cache-root
  prewrite shadows the live transient encode, and undeletable debris masks a
  valid transient entry into a from-zero re-encode
status: Done
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - cache
  - regression
dependencies:
  - JF-537.1
priority: medium
---

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

## Description

## Notes

<!-- SECTION:NOTES:BEGIN -->
DESIGN (worker, 2026-10-05). FINDING 1 (VideoAudioCache.FindHlsDirectoryByScan):
both roots' newest generations are compared by CreationTimeUtc, the cache root
winning ties; ScanRootForNewestGeneration now returns the DirectoryInfo so the
comparison is stat-free. FINDING 2 (the prewrite shadow): the reviewer's SECOND
fix shape chosen (the probe prefers the live encode's registered directory),
not the 1032-backstop-deletes-the-prewrite shape, because the latter is
untestable against the filed bar: every DELETABLE stale prewrite is already
removed by an earlier whole-dir cleanup (CleanupHlsStub when no stream.m3u8
survives, the verdict's CleanupHlsGenerationAt when one does), so the only
production class that reaches the backstop with the prewrite present is the
UNDELETABLE class, whose per-file deletes fail exactly like the recursive
ones; the pin therefore plants the stale prewrite mid-window (after the
encode's own cleanups ran) and asserts the serve. FINDING 3 (the debris mask):
the reviewer's first fix shape (probe the transient root when the cache-root
hit fails its verdict cleanup); probing the transient root after ANY null
cache-root verdict, not only survived-cleanup ones, because serving a valid
transient entry is strictly better than re-encoding in both cases and the
extra probe on the cleaned case is one File.Exists.

REVIEW ROUND (high code-review, all applied except F4): F1 revealed the
registration signal lagged (registered only after the first-segment wait), so
the probe's preference was inert in the mark-to-register window and could
serve a PREVIOUS encode's stale registration when a cap change flipped the
next encode's root; fixed by registering at ENCODE START and by making the
live-override exclusive (while the live encode owns the transient generation,
that dir is the ONLY prewrite source; absent prewrite falls back to the live
playlist, never the other root's stale listing). F2 proved the in-lock
double-check's "deliberate omission" comment's invariant false (a transient
entry minted by the concurrent lock-holder after the fast-path probe is
newly visible at the double-check): the transient leg now runs at BOTH rows,
after the breach guard so a pin breach stays loud. F3: the own-live warm
serve's no-prewrite fallback now serves the live encode's REGISTERED dir's
stream.m3u8 (contained to the caller's own generation dirs) instead of the
verdict's first-hit file, which under the split can be the other root's
stale bytes. F5: RunWithDeniedDirectoryAsync asserts the write-denial HELD
(the directory survived), so a root runner reds instead of silently
degrading the undeletable-class pin. F6 (the duplicate candidate stat)
resolved structurally by the exclusive-branch probe. F4 (the one-owner
liveness-aware resolver) SKIPPED and FILED as JF-775 (the pinned
byte-identical serve rows and the JF-650 re-arm discipline; its runtime
windows are closed by F1-F3, the residual is the architectural
consolidation).

FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-05 by the orchestrator from the JF-537.1 gate-marker (all three
findings are NEW exposure created by the two-root design; the single-root
pre-diff code self-healed each shape by construction). The reviewer verified
every other axis mechanically (the structural sweep exclusion, the reaper's
placement and pin-honoring, the JF-428 contracts untouched, both red halves)
and re-ran the changed battery 45/45 both TFMs; these three are the residual
edge class, filed with the reviewer's precise fix shapes rather than blocking
the landing.

FINDING 1 (the most reachable; needs only a surviving old cache-root generation
plus a restart, no undeletable class): FindHlsDirectoryByScan's
cache-root-first preference outranks generation recency ACROSS roots
(VideoAudioCache.cs ~1219). After a media change re-keys the item (t2) and an
oversize t2 play mints the transient root while the old cache-root {E}_t1 dir
lingers as the documented JF-676 orphan, a post-restart GetSegment falls back
to the scan and resolves the OLD t1 dir purely because it exists; segments
serve wrong content or 404 past the old cut, and the wrong dir pins for the
process lifetime. FIX: pick the newest generation across BOTH roots (compare
CreationTimeUtc), the cache root as tie-break, matching the doc's own
per-root-most-recent intent.

FINDING 2 (the undeletable class): the transient leg's cache-root per-file
backstop (~VideoAudioController.cs:1505 serving the 1032 backstop's gap)
deletes stream.m3u8 and seg_*.ts but leaves playlist-full.m3u8, and the
cache-first prewrite probe serves any existing file unvalidated; a stale
prewrite with an expired JF-309 token 401s every segment fetch for the whole
encode window (~27min CPU for a 2h episode at the measured 4.4x). FIX: the
1032 backstop also deletes the stale prewrite, or the prewrite probe prefers
the live encode's registered directory.

FINDING 3 (the undeletable class): an undeletable cache-root debris playlist
that keeps winning the cache-first probe sends the fall-through re-encode into
DeleteHlsEncodeDebris (~:1020) which wipes the VALID same-key transient entry
the probe never reached: a from-zero multi-GB re-encode, plus the emptied
cache-root dir feeds Finding 1's scan shape post-restart. FIX: probe the
transient root when the cache-root hit fails its verdict cleanup, or scope the
debris delete to the cache root only.

VERIFICATION BAR: one pin per finding (1: the orphan-plus-restart shape
serving the NEW generation's segments; 2: the stale-prewrite shape starting
playback during the encode window; 3: the debris-plus-valid-transient shape
serving without re-encode), red on the current tree.

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker 06ebf585 + tails 8ce81a53 and 40d31fc0, --no-ff; the gate-marker's six axes PASS with the red proofs independently reproduced in a throwaway base worktree; the four simplify angles CLEAN with the altitude reshuffle applied), combined-tree suite 5257/5257 both TFMs. NOTE: the MCP status-flip hit the ENAMETOOLONG slug limit AND left the file deleted in the working tree (the JF-724 casualty class); restored from d0d69087 (the file already carried status Done from the worker's own edit) with this closure note appended by hand.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED (worker branch, not merged): the three same-root self-healing
shapes the JF-537.1 two-root split broke, each with the reviewer's fix shape:
(1) FindHlsDirectoryByScan picks the NEWEST generation across BOTH roots by
CreationTimeUtc (cache root winning ties), so a post-restart GetSegment serves
the current generation's segments instead of the old cache-root orphan's;
(2) the episode prewrite probe, while the caller's own encode is LIVE and
registered at the transient generation dir, serves ONLY that dir's listing
(a same-key cache-root playlist-full.m3u8 is necessarily stale debris of the
other root whose expired JF-309 token would 401 every segment fetch for the
whole encode window), with the registration moved to ENCODE START so the
signal covers the full serve window; (3) both episode warm-cache rows (fast
path and in-lock double-check) probe the transient root after a cache-root
verdict null, so an undeletable cache-root debris playlist can no longer mask
a valid same-key transient entry into a from-zero re-encode that the encode
branch's per-file debris sweep would then wipe. RED PROOF (ran on the
UNMODIFIED base first, net9.0): all three pins red with the predicted failure
modes (the scan resolving the t1 orphan; the replay serving the stale
JF774-STALE-EXPIRED prewrite; the re-encode's fresh prewrite serving instead
of the seeded 4.444 transient playlist with the run counter incremented).
TESTS: 3 new pins (GetSegment_AfterRestart_ScanPrefersNewestGenerationAcrossRoots
with a fresh-cache-over-same-path restart simulation and explicit creation
times; StreamHlsEpisode_LiveTransientEncode_StaleCacheRootPrewrite_DoesNotShadowFreshPrewrite
with a parked fake, mid-window stale plant, and the fresh prewrite's token as
the discriminator; StreamHlsEpisode_UndeletableCacheRootDebris_DoesNotMaskValidTransientEntry
with the JF-499 W4 write-denied idiom and its denial-held assert). GATES:
/simplify (4 parallel angle agents; 6 APPLIED incl. the JF-679-shaped local
TryServeEpisodeCacheAsync serve-row function over the three sites, the async
denied-directory sibling, the WriteRunCountingFakeFfmpeg fake family fold,
and the FindHlsDirectory predicate single-homing; 3 SKIPPED with reasons);
/code-review high (6 findings: F1/F2/F3/F5 APPLIED incl. the encode-start
registration move, the exclusive live-override, the in-lock transient leg,
and the own-live registered-dir fallback; F6 resolved structurally; F4 FILED
as JF-775). SUITES: 5248/5248 BOTH TFMs (5245 baseline + 3 pins), full run
on the final state; Release --no-restore -warnaserror 0 warnings 0 errors
(the round caught and fixed one xUnit1030 ConfigureAwait in a test method).
DoD: #1/#3 the Release build above; #2 5248/5248 both TFMs; #4-#8 N/A (no
session-attribute, HttpClient, model, handler, or locale surface touched);
#9/#10 the two gates above. NOT DEPLOYED (worker branch only).
<!-- SECTION:FINAL_SUMMARY:END -->

