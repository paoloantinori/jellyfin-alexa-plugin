---
id: JF-774
title: >-
  JF-774 - the two-root split (JF-537.1) broke three same-root self-healing
  shapes: the cross-root scan ignores generation recency, a stale cache-root
  prewrite shadows the live transient encode, and undeletable debris masks a
  valid transient entry into a from-zero re-encode
status: To Do
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

## Description

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
cache-first prewrite probe serves any existing file unvalidated - a stale
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
