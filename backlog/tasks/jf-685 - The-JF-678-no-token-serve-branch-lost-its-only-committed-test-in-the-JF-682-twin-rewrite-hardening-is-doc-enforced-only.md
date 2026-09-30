---
id: JF-685
title: >-
  JF-685 - the JF-678 no-token serve branch lost its only committed test in the
  JF-682 twin rewrite; its materialization hardening is doc-enforced only
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - encode-gate
  - test-gap
dependencies:
  - JF-682
references:
  - >-
    backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
  - >-
    backlog/tasks/jf-678 - JF-678-token-less-serve-skips-the-JF-499-W3-vanish-probe-PhysicalFile-over-a-vanished-playlist-500s-at-result-execution.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-682 code-review round (high effort, finding 1 of 5).

THE GAP: `StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode` was the ONLY committed test driving `ServePlaylistWithTokenAsync`'s no-token branch (materialized read via `ResolveServeContentAsync`, vanish-to-re-encode fall-through), and JF-682 rewrote it in place into the 503 pin (`StreamHlsAudiobook_SecretEmptiedAtChapterRemint_ServesGate503_NoReencode`) because the 503 closed the branch's last production driver. Since JF-682 the branch is reachable by NO test construction through the suite's public-endpoint idioms: every public HLS entry is gate-gated (401 no token / 503 empty secret) and the redirect's overrideToken can no longer be empty. A future edit reverting the branch to a raw `PhysicalFile` (or dropping `ResolveServeContentAsync` there) re-introduces the exact JF-678(a) bug (a 500 at result execution over an evicted playlist) with all 4784 tests staying green. The one-time manual red proof documented in the twin's doc is not a committed guard.

CANDIDATE CONSTRUCTIONS (the trade-off this task must decide): (a) an internal seam (make the serve family or a driver internal + InternalsVisibleTo-style test access) so a test can drive the branch through a real construction; (b) reflection-invocation of the private method (works today, novel fragile pattern for this suite); (c) accept the branch as a documented safety net with no committed pin (a decision, recorded, not a fix). The JF-682 dispatch explicitly chose the rewrite-to-503 shape, so this task owns the residual, it is not a regression JF-682 introduced beyond the trade-off already taken.
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
