---
id: JF-783
title: >-
  JF-783 - the generation-scoped HLS registration map grows one inert entry
  per encoded generation for the process lifetime (removal belongs with the
  eviction sweep)
status: Done
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - cache
  - tech-debt
dependencies:
  - JF-782
priority: low
---

## Description

Filed 2026-10-05 from the JF-782 /code-review high round (its finding 4),
per the same-turn landing rule.

JF-782 leg 3 added `_hlsGenerationDirLookup`, the (key, art-ticks)-keyed
HLS directory registration the liveness-aware resolver's exclusive arm
reads. Neither it nor its per-key twin `_hlsDirLookup` has ANY removal
path (grep: TryRemove is only ever called on _pinnedPaths,
_lastAccessUtc, and _itemLocks), which for the per-key map is the
long-standing accepted shape (one entry per key, displaced by the next
registration). The generation map's bound is strictly weaker: one entry
per DISTINCT encoded generation, so every album-art refresh (a new
artModifiedTicks) plus every distinct item ever encoded mints a permanent
entry. Entries whose directories were evicted or deleted stay resident:
`TryGetRegisteredGenerationHlsDirectory` only nulls its ANSWER via the
Directory.Exists re-check without removing the mapping. A long-lived
Jellyfin process with an art-refresh routine or a rotating library
accumulates unbounded dictionary growth (small records, but unbounded),
unlike the eviction-tracked state (_lastAccessUtc, _pinnedPaths) in the
same class.

The cost today is negligible per entry and the JF-782 efficiency review
cleared the steady-state behavior; this is filed so the growth bound is a
tracked decision rather than an accident.

FIX SHAPE (for whoever picks this up): removal belongs with the eviction
and cleanup owners, not with the serve-path readers. Candidates, in
rising effort: (a) prune in the eviction sweep (it already walks
directories and knows what it deleted; the map needs a
key-from-dir-name derivation or a value-matched removal), (b) prune in
`CleanupHlsGenerationAt` (the verdict's scoped delete; same key-derivation
problem: it receives a directory path, not (key, ticks)), (c) cap at the
write side (drop other same-key entries whose directory no longer exists
when registering; O(map) scan per encode, only sound if done lazily).
Watch the invariant: a removal may NOT race the register-before-mark
window (a pruned entry for a live generation re-registers on its next
encode, but the zero-slot window reads it), so any removal must be
content with inert-after-delete entries or hold the per-item lock.

VERIFICATION BAR: a pin that encodes N generations of one key (distinct
art ticks), deletes their directories, and asserts the generation map's
entry count (needs a test seam on the count or an internal reader);
red on the pre-fix tree, green with the chosen removal path.

## Implementation record (2026-10-05)

THE SHAPE: filing candidate (a) generalized to "every deterministic
generation-directory delete in VideoAudioCache drops the dead
registration". ONE private helper, `UnregisterHlsGenerationDirectoryAt(
dirPath)`: a value-matched walk of `_hlsGenerationDirLookup` (Ordinal
path equality against the registered value) ending in the
remove-if-value-matches `TryRemove(KeyValuePair)` overload, guarded by a
`!Directory.Exists(dirPath)` re-check so a directory RE-CREATED between
the delete and the walk (a live encode of the same generation already
re-registered it) keeps its fresh registration. Value-matched, not
key-derived: the delete sites hold a directory PATH, the registered value
IS the authoritative path (root included), and deriving (key, ticks) back
out of the dir name would re-encode the name format a second time (and
silently miss any future resolved-path registration). Wired at FOUR
delete sites (the delete-site census: all five `Directory.Delete` calls
in the class, grep-verified; the controller has zero direct generation-
dir deletes, everything routes through the cache):
1. the cap eviction sweep's directory arm (the filing's prescribed (a)),
2. `CleanupHlsGenerationAt` (filing (b): the debris verdicts AND the
   transient-root idle reaper, that root's ONLY delete owners since the
   cap sweep skips the transient subtree, without which the transient
   half of the map would stay unbounded),
3. the manual `Cleanup(itemId)` wipe's directory arm,
4. `CleanupHlsStubInDir`'s two stub branches (code-review F2: NOT pure
   churn; the in-lock early-return serve branch, a probe hit in the other
   root returning without ever re-registering, would strand the dead
   entry for the process lifetime). Safe to wire because the per-item
   lock is (key, ticks)-scoped, so no concurrent same-generation
   registration can be in flight, and the encode branch's fresh
   registration lands moments later under the same lock and overwrites.
All four call sites are success-only (inside the try, after the delete
returned), unlike the sweep's unconditional `_lastAccessUtc` drop: a
FAILED delete leaves a live directory whose entry is still the O(1)
resolution of a servable generation.
Reader-observability: the map's only reader
(`TryGetRegisteredGenerationHlsDirectory`) re-checks Directory.Exists,
so a contract-obeying removal answers identically before and after; the
per-key `_hlsDirLookup` twin keeps its accepted one-entry-per-key
no-removal shape. The field's GROWTH BOUND doc rewritten: the map is now
bounded by the generations whose directories are still on disk plus
out-of-band deletions (an operator rm outside the plugin), whose entries
stay inert exactly as before.
TEST SEAM: `internal int HlsGenerationRegistrationCount` (the count
WITHOUT the existence re-check; the reader cannot discriminate "removed"
from "inert-null").

RED PROOF: seam + pins landed first, run on the UNMODIFIED production
tree, all 3 red on the counts (Expected 1 / Actual 2; Expected 0 /
Actual 1; Expected 1 / Actual 2), then the removal wired, 3/3 green.
Post-review pins: +2 (stub arm, manual-wipe arm). PINS (5, in
VideoAudioCacheTests):
- `EvictIfNeeded_EvictedGenerationDir_ItsRegistrationIsRemoved` (the
  mandated red-proof: two generations of ONE key, the old dir evicted,
  count 2 to 1, the survivor still resolves through the exclusive-arm
  reader, the removed one reads like the old inert null),
- `CleanupHlsGenerationAt_DeletedDir_ItsRegistrationIsRemoved` (the
  scoped-delete arm; transient root),
- `EvictIfNeeded_UndeletableGenerationDir_RegistrationRetained` (the
  removal contract's live half: a ReadOnly-undeletable dir keeps its
  entry, the deletable sibling's goes),
- `CleanupHlsStub_RemovedStubDir_ItsRegistrationIsRemoved` (F2),
- `Cleanup_KeyWideWipe_RemovesAllGenerationRegistrations` (F3).
Guard families re-run filtered: the JF-775/JF-782 registration/resolver
controller pins 18/18 both TFMs; the full cache class 51/51.

SUITES: 5319/5319 BOTH TFMs on the final state (worktree JF-781-tip
lineage + 5 pins); Release `--no-restore -warnaserror` clean, 0 warnings.

GATES. /simplify (4 parallel angles): 3 applied (the `TryRemove(pair)`
on the observed pair instead of KeyValuePair reconstruction, the
`SeedCacheGenerationDir` cache-root seeding twin of
`SeedTransientGeneration` deduplicating the pins' seed-and-age blocks,
and the stub-arm doc note); 1 reasoned skip (folding the undeletable pin
into the pre-existing `EvictIfNeeded_UndeletableEntry...` test: the
JF-783 removal contract keeps its own pin for the red-proof lineage, and
the seeding helper absorbed most of the mirror cost); efficiency and
altitude angles returned clean (the O(map) walk rides delete paths only,
dwarfed by the recursive delete it follows; derived-key O(1) judged
fragile for the same format-re-encoding reason the helper documents).
/code-review high (8 angles): 3 findings, ALL 3 applied (F1 the
delete-to-walk race can swallow a concurrent SAME-PATH re-registration
since remove-if-value-matches only protected the different-directory
root-switch case: fixed with the `!Directory.Exists` guard and the doc's
absolute never-pruned claim sharpened to name the residual straight-line
window; F2 the stub-arm non-wiring justification falsified by the
in-lock early-return serve branch: wired, plus its pin; F3 the
Cleanup-wipe arm untested: pinned). No findings filed as JF-785 (none
out of scope).

Production: not deployed (worker branch only; cache-internal bookkeeping,
no device-observable behavior change by the reader-observability argument
above).

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Debug + Release --no-restore -warnaserror, 0 warnings 0 errors)
- [x] #2 dotnet test passes (5319/5319 BOTH TFMs on the final state)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: cache-internal bookkeeping, no intent/handler change; pinned at unit level, 5 pins)
- [x] #8 Locale response strings added to all 17 locales (N/A: no response strings touched)
- [x] #9 /simplify passed (3 applied, 1 reasoned skip, recorded above)
- [x] #10 /code-review high passed (3 findings, all 3 applied, recorded above)
<!-- DOD:END -->
