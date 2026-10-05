---
id: JF-783
title: >-
  JF-783 - the generation-scoped HLS registration map grows one inert entry
  per encoded generation for the process lifetime (removal belongs with the
  eviction sweep)
status: To Do
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
