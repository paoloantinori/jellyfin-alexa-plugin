---
id: JF-859
title: >-
  JF-818's three simplify-round follow-ups rehomed: the latch reset via
  encode-registry flag, the episode planter's AllPlantedSegments shape, and the
  EXTINF walk on the span-scan
status: To Do
assignee: []
created_date: '2026-10-10 12:58'
labels:
  - tech-debt
  - videoaudio
  - follow-up
dependencies: []
references:
  - >-
    backlog/tasks/jf-818 -
    the-windowed-prewrite-ORCHESTRATION-now-exists-in-two-families-episode-audiobook-extract-the-shared-serve-core-and-drop-the-duplicate-ResolveStartSegment-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from reconciliation round 2: JF-818's hoist LANDED 2026-10-09 (merge d4687171, verified ancestor of HEAD; baseline 335 = post-hoist 335 with zero pin edits, suites 5569/5569 both TFMs) but its status stayed To Do while the file also tracks THREE genuinely open follow-ups from its /simplify round. This task rehomes them so JF-818 can close honestly:

(1) LATCH RESET VIA ENCODE-REGISTRY FLAG: the windowed-prewrite latch (per the landed ServeWindowedPrewriteAsync core) resets by a mechanism that should derive from the encode registry's own state, not a parallel signal.
(2) EPISODE PLANTER'S FixedTwoEntries SHAPE: the planter pins a fixed-two-entries expectation that should become AllPlantedSegments (span-derived, not count-hardcoded).
(3) EXTINF WALK SPAN-SCAN: the resume transition's EXTINF walk should use the landed TruncateToFirstSegments span-scan instead of its own walk (the JF-775-class consolidation instinct: one span owner).

Read JF-818's Implementation Notes for the full context of each; they were sized as small, independent touches by that round.
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
