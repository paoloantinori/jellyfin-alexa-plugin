---
id: JF-676
title: >-
  JF-676 - own-dead + foreign-live serve window can serve a KILLED own-ticks
  encode's stale no-ENDLIST playlist (needs a ticks-scoped debris verdict)
status: In Progress
assignee: []
created_date: '2026-09-29 22:12'
labels:
  - encode-gate
  - race
  - cache
dependencies: []
references:
  - >-
    backlog/tasks/jf-675 -
    JF-675-own-ticks-slot-liveness-at-the-prewrite-serve-gates-completed-foreign-ticks-caches-serve-no-ENDLIST-prewrites-verdicts-registry-family-boundary.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-675 code-review round (high effort, finding 1 of 3; F2 and F3 were doc-accuracy findings and were applied in the same change).

THE RESIDUAL WINDOW: JF-675 made the four prewrite serve gates test the CALLER'S OWN art-tick slot, so an own-dead + foreign-ticks-live entry falls through to the normal cache serve. That is exactly right when the own-ticks encode COMPLETED (ffmpeg's ENDLIST playlist is served immediately). But when the own-ticks generation was KILLED mid-encode (the stall killer, or a first-segment failure's KillAndClear) its directory keeps a partial stream.m3u8 (no ENDLIST) and the monitor only logs, and `ValidateEpisodeCacheAsync` short-circuits on any-generation presence (correctly: its Cleanup is key-wide, and the conservative verdict is what protects the FOREIGN generation's live directory). In that window the episode path serves the dead partial playlist: ExoPlayer joins mid-content at the dead live edge and live-edge-polls a playlist that never grows (the song-path twins have no debris validation at all, same shape).

Reachability: art change mid-encode + the own-ticks encode killed (not completed) + a same-ticks playlist fetch during the foreign generation's remaining duration; self-heals when the foreign generation exits (the debris verdict then cleans and the next fetch re-encodes). Pre-JF-675 this window served the full pre-write listing instead (a degraded but event-shaped serve).

FIX DIRECTION (from the review): the gap needs a TICKS-SCOPED validation/cleanup - a debris check (and directory cleanup) scoped to the caller's (key, artTicks) directory, runnable in the own-dead fall-through without endangering the foreign generation's directory (the key-wide Cleanup cannot be re-run there). Do NOT band-aid it by serving the prewrite whenever the cached playlist lacks ENDLIST: that re-widens the completed-encode serve JF-675 just fixed whenever a foreign generation runs.

VERIFICATION: a pin with gen A (ticks A) parked live, gen B (ticks B) killed after writing a partial playlist (fake ffmpeg exits nonzero on a stop file), then a ticks-B fetch must not serve the dead no-ENDLIST playlist (re-encode or cleaned serve), while the JF-669 cross-tick debris pin and the JF-675 completed-own-ticks pin stay green.

ADDENDUM A (severity nuance, verified by the JF-675 orchestrator gate-marker round 2026-09-29): the pre-JF-675 behavior in the own-dead window was ALSO broken. Per the project's JF-625 evidence, the old prewrite serve in that window 404s the promised-but-missing tail segments and kills the player (green first frame, no audio, no controls); the prewrite is only safe while the encode outlasts the device's first playlist fetch, which a KILLED encode does not. So JF-675 is a WASH in-window (one broken degraded serve swapped for another), not a regression: JF-676 fixes a long-standing broken window, not a newly-introduced one.

ADDENDUM B (stronger sibling finding, same review): `ValidateAudiobookCacheAsync` (VideoAudioController.cs ~4740-4760) fires the SAME key-wide `_cache.Cleanup(parentId)` with NO liveness short-circuit AT ALL: an ENDLIST playlist with fewer segments than the chapter count triggers the wipe unconditionally, so a foreign-ticks audiobook generation live-writing its directory can have that directory deleted mid-write by a request validating an older, undercounting ENDLIST playlist of the same key. This is SAME-REGISTRY and strictly stronger than the cross-registry theoretical case the JF-675 boundary doc names (the audiobook registry exists and is simply never consulted by the validator; the episode validator at least short-circuits on its own registry's presence). RECOMMENDED SCOPE: JF-676's ticks-scoped verdict should cover BOTH validators - the episode `ValidateEpisodeCacheAsync` and the audiobook `ValidateAudiobookCacheAsync` - plus, per the original filing, the own-dead fall-through on all four prewrite serve gates.
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
