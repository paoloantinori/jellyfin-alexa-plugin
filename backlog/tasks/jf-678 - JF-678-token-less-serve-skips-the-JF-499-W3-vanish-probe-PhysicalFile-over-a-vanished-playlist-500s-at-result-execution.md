---
id: JF-678
title: >-
  JF-678 - token-less serve skips the JF-499 W3 vanish probe: PhysicalFile over
  a vanished playlist 500s at result execution
status: Done
assignee: []
created_date: '2026-09-30'
updated_date: '2026-09-30 15:16'
labels:
  - encode-gate
  - robustness
dependencies: []
references:
  - >-
    backlog/tasks/jf-677 -
    JF-677-double-playlist-read-at-every-validated-warm-cache-serve-verdict-reads-the-full-file-serve-rereads-it.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-677 code-review round (high effort, finding 2 of 5), the pre-existing gap the review surfaced while auditing the new vanish probe.

THE GAP: `ServePlaylistWithTokenAsync` (and the serve family generally) returns `PhysicalFile(playlistPath, ...)` on the NO-TOKEN branch BEFORE `ResolveServeContentAsync` ever runs, so a token-less serve holding a verdict-validated (or fresh) path performs NO vanish probe and NO read. When the playlist directory is deleted between the cache/verdict read and result execution (a concurrent debris verdict or eviction sweep, the JF-499 W3 race), the exception fires at RESULT-EXECUTION time, outside `TryServeValidatedHlsCacheAsync`'s catch: a 500 that only self-heals on the Echo's playlist retry, exactly the shape JF-499 W3 was filed to remove for the content branches.

SCOPE AND REACHABILITY: the skill always mints `?token=` on its URLs (JF-309), so the exposure is non-skill/manual fetches; the review also named the single-chapter audiobook redirect with an empty `StreamTokenSecret` as a reachable shape. Behavior is UNCHANGED by JF-677 (the old sync serve had the same early PhysicalFile return), which is why JF-677 documented the exception instead of widening its diff.

FIX DIRECTION: run the same existence probe on the no-token branch when preloaded content or a preceding verdict exists (or unconditionally), throwing the same `FileNotFoundException` into the existing vanish-translation paths; needs its own red proof (a token-less vanish-at-serve pin, mirroring the FastPathCacheVanishedAtServe pair) because it is a behavior change (500-at-execution becomes re-encode fall-through), and a decision on whether the token-less branch deserves the fall-through at all (a manual fetcher might prefer the 404/500 over silently kicking a re-encode).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-30 as merge 8e3609ce on main (deployed with the full checklist, route smoke green): the whole vanish-at-serve family hardened under ONE idiom. THROW half: ProbePlaylistExists is the single existence probe whose canonical FileNotFoundException fires at action time (used by the threaded path and the no-token raw serve, which now materializes content so it is immune at result execution; the conditional-GET loss is an accepted cost). TRANSLATE half: TryServeValidatedHlsCacheAsync's catch carries all eight verdict rows, whose null falls through to the caller's own encode branch. THE BREACH GUARD: GuardInLockVanishFallThrough at all four in-lock rows makes the design's breach-loud rule real: a vanish while the caller's own generation is STILL LIVE is a JF-428 pin breach and fails the request loud with a breach-named warning (the silent double-encode against the live writer is gone); no live generation is the legitimate stale-debris fall-through. The audiobook content branches carry !IsHlsVanishException filters with the existenceConfirmed boundary (the album first-fetch flush-lag row degrades as before and is pinned; the pin caught the worker's own filter inversion in development). Documented residuals: prewrite/first-fetch unwrapped sites (breaches surface loud at action time), the MP4 sibling (self-heals as a cache miss), the conflation decision kept with honest wording, the doc consolidation to one authoritative account, and JF-682 owning the secret-shape wasted-encode. ELEVEN twins with red proofs (7 first-round + 4 breach + the flush-lag pin). Gates: Skill simplify (6 applied in-worker; the orchestrator pass on the final state found only the duplicated no-token clause, merged in the tail; the altitude agent died on infrastructure AFTER delivering its clean three-probe verdict) + Skill code-review high in-worker (4 applied) + TWO orchestrator gate-marker rounds (base: 5 findings all landed in the rework incl. the breach guard; rework: 6 applied). Suites: 4779/4779 pre-rework, 4784/4784 post-rework and merged-tree, both TFMs, worker-run and orchestrator-verified independently (vanish roster 15/15, filtered 6/6 on the tail state). Live surface: the hardened rows only misbehave on deletes mid-serve (not device-drivable); the deploy smoke is the route probe plus config integrity.
<!-- SECTION:FINAL_SUMMARY:END -->
