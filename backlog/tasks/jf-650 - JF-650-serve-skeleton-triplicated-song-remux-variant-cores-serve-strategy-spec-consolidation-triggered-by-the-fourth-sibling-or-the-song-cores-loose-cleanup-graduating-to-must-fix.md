---
id: JF-650
title: >-
  JF-650 - serve skeleton triplicated (song/remux/variant cores):
  serve-strategy-spec consolidation, triggered by the fourth sibling or the song
  core's loose cleanup graduating to must-fix
status: To Do
assignee: []
created_date: '2026-09-27 08:20'
labels:
  - tech-debt
  - refactor
  - streaming
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 altitude review (consolidation landed with the two tight siblings absorbed; the loose siblings are verified genuinely different, so the residual triplication is now a NAMED follow-up, not an undocumented gap).

THE RESIDUAL: the ~80-line serve skeleton (cache-hit serve, encode-in-progress concurrent serve, gate, start, first-segment wait, monitor, post-encode serve) now exists three times: the song core, the remux core, and the new shared variant core (ServeVariantHlsAsync). The altitude review verified the absorption blockers are REAL: (a) flag-vs-prewrite ordering differs per path and is load-bearing (song: flag AFTER prewrite so the flag implies listing-on-disk; remux: flag BEFORE process start with the prewritten-playlist fallback covering the race; variant: no prewrite); (b) registries and serve calls differ (_activeVideoAudioEncodes + overrideToken for song; ServeEpisodePlaylist(startTicks) for remux; _activeEpisodeEncodes + ServePlaylistWithToken for variant); (c) the song core has NO debris-cleanup step and no JF-499 vanish-guard on its fast path (it is looser than the family doc implies); (d) remux carries its own transcode tier, JF-537 cap warning, and extended monitor args.

THE HONEST DEEPER SHAPE (named by the review): a serve-strategy spec (TryServeWhileEncoding, prewrite-after-start, ServeAfterEncode delegates + registry + token-override members), absorbing the three skeletons with the ordering differences expressed as strategy hooks instead of delegates-per-site. Natural trigger: the next variant-HLS endpoint (fourth sibling), or whenever the song core's missing debris-cleanup/vanish-guard (item c) graduates from accepted-loose to must-fix (it is a real gap: the song path can leave debris and cannot recover a vanished cache dir the way the variant path can).

SCOPE GUARD: do NOT attempt this before JF-537.1 (cache-rooted transient mode) lands or is declined; both touch the same serve paths and the strategy refactor should absorb the final shape, not an intermediate one.
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
