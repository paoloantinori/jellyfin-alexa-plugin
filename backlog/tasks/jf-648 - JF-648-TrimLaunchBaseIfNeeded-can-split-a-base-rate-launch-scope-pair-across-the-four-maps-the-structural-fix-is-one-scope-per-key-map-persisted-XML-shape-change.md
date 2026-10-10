---
id: JF-648
title: >-
  JF-648 - TrimLaunchBaseIfNeeded can split a base/rate launch-scope pair across
  the four maps; the structural fix is one scope-per-key map (persisted XML
  shape change)
status: Done
assignee: []
created_date: '2026-09-27 08:16'
updated_date: '2026-10-10 18:42'
labels:
  - device-queue
  - tech-debt
  - persistence
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 review round (the worker's bugs-noticed list; pre-existing).

THE DEFECT: DeviceQueue.TrimLaunchBaseIfNeeded evicts the four launch-scope maps (base/rate pairing across Pending/Active) INDEPENDENTLY, each by its own insertion order, under the ~200-entry cap. A legacy base-without-rate pair whose halves entered the maps at different times can be SPLIT by trimming (base evicted, rate orphaned, or vice versa), breaking the 'pending rate exists iff pending base exists' invariant at the margins.

STRUCTURAL FIX (named by two independent reviewers): collapse the four maps into one scope-per-key map (Dictionary<string, LaunchScope> with a nullable rate), so trimming is inherently atomic per scope and the invariant becomes structural rather than convention-enforced. The JF-637 round already moved every write site behind WritePendingLaunchScope/WriteActiveLaunchScope/RetirePendingLaunchScope, so the migration surface is those three helpers plus the readers (GetActiveLaunchScope, GetActivePlaybackRate) and the XML persistence shape (WARNING: the persisted queue XML shape changes; a migration or version bump is needed so existing persisted queues survive, longest-file-wins principles on conflict).

INTERIM NOTE until fixed: the JF-637 helpers' docs honestly record that trim/copy survive as convention-enforced sites.
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
