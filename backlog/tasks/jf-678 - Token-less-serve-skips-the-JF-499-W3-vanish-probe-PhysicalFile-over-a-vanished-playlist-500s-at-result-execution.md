---
id: JF-678
title: >-
  JF-678 - token-less serve skips the JF-499 W3 vanish probe: PhysicalFile
  over a vanished playlist 500s at result execution
status: To Do
assignee: []
created_date: '2026-09-30'
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

ORCHESTRATOR GATE-MARKER EXTENSION (2026-09-30, same-turn from the JF-677 marker round): this task now owns the WHOLE vanish-at-serve hardening family surfaced by that round, not just the no-token branch probe: (1) the three in-lock verdict+serve rows (song ~534, episode ~889, variant ~1916) let the probe's FileNotFoundException propagate unhandled to a bare 500 on the verdict-to-serve race (pre-existing; the old fresh read propagated identically); (2) the two audiobook content-branch sites' generic catch falls through to PhysicalFile over the now-vanished path, a 500 at result execution outside every catch; (3) the song path has NO vanish-at-serve pin at all (the FastPathCacheVanishedAtServe pair covers episode + variant only) - the song twin belongs with whatever hardening lands. Also note for the fixer: the probe's File.Exists conflates inaccessible with vanished (an ACL revocation between verdict and probe becomes a silent vanish translation -> CleanupHlsStub + re-encode attempt on an unreadable dir, where the old read's UnauthorizedAccessException failed loudly) - decide deliberately whether the loud failure is wanted back.
