---
id: JF-675
title: >-
  JF-675 - own-ticks slot liveness at the prewrite serve gates (completed
  foreign-ticks caches serve no-ENDLIST prewrites) + verdict's registry family
  boundary
status: In Progress
assignee: []
created_date: '2026-09-29 18:49'
updated_date: '2026-09-29 19:10'
labels:
  - encode-gate
  - playback-speed
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-669 -
    Active-encode-flag-keying-conflates-art-tick-generations-a-live-older-ticks-encode-can-be-declared-debris-after-a-newer-ticks-encode-finishes-first.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-669 orchestrator gate-marker round (findings 1 and 2; the scrutiny questions - retry termination, mid-registration window, no masking - all passed).

FINDING 1 (the widened prewrite window): JF-669's any-generation presence semantics widen the prewrite-serve window for a COMPLETED foreign-ticks cache from the monitor-clear lag (seconds) to the sibling encode's entire remaining duration. Scenario: gen A (ticks A, long content) live; gen B (ticks B) completes and writes ENDLIST; every subsequent ticks-B playlist fetch serves dir_B's static no-ENDLIST prewritten listing instead of ffmpeg's ENDLIST playlist for as long as gen A keeps the flag up (pre-JF-669, the flag dropped when B's monitor cleared - the debris bug whose side effect was incidentally the correct serve shape here). Consequence: ExoPlayer reaches the tail of completed content, finds no ENDLIST, and keeps live-edge polling for growth that never comes until gen A finishes. The JF-669 task notes' "identical to today's bare-key behavior: no regression" claim is inexact for this dimension (it holds for the flag-liveness dimension only).

FIX DIRECTION (named by the review): the four prewrite serve gates (song fast/in-lock, episode fast/in-lock, ~460/~489/~782/~818 in the JF-669-era numbering) could first test the CALLER'S OWN ticks slot liveness (ActiveEncodeGenerations exposing slot-liveness) and fall back to any-generation presence only for the conservative directions (the debris verdict, the holds, the guards), so a completed own-ticks encode serves its ENDLIST playlist immediately while cross-ticks liveness still protects directories. Needs its own pin: gen A live, gen B completed, a ticks-B fetch must receive the ENDLIST playlist.

FINDING 2 (latent family-boundary asymmetry, pre-existing): ValidateEpisodeCacheAsync's liveness gate reads only _activeEpisodeEncodes while its _cache.Cleanup(itemId) wipes every {guid}_* directory of the key, which a song-registry encode for the same GUID could be live-writing (StreamHlsVideoAudioCore names directories in the same cache root); TryHoldForNearAheadSegmentAsync already treats liveness as any-of-three-registries, so the asymmetry is real but the routing overlap is theoretical today (handlers route audio to the song path, episodes to the episode path). A doc sentence on the verdict's registry-family boundary belongs with whatever fix lands (or as a standalone doc note if the fix is deferred).

VERIFICATION: the new pin(s) + the JF-669 cross-tick debris pin + the JF-665/668/647 pins all stay green; no same-ticks serve behavior changes (the overwhelmingly common case must stay byte-identical).
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
