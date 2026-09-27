---
id: JF-649
title: >-
  JF-649 - unbalanced CA3003 pragma in VideoAudioController widens a suppression
  zone past its intended scope (one-line restore)
status: To Do
assignee: []
created_date: '2026-09-27 08:16'
labels:
  - analyzer-hygiene
  - trivial
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 review round (the worker's bugs-noticed list; pre-existing, trivial).

THE DEFECT: Controller/VideoAudioController.cs carries one unbalanced CA3003 pragma pair (a disable near the audiobook concurrent-serve guard around line ~2094 with no matching restore; the file counts 39/38 disables/restores before this site, 37/36 after). The missing restore silently widens that suppression zone to the NEXT restore in the file, suppressing file-access analyzers over unrelated code that follows.

FIX: add the matching #pragma warning restore CA3003 at the intended end of the audiobook concurrent-serve guard's scope; then verify the count balances (grep -c 'disable CA3003' == grep -c 'restore CA3003') and the build stays 0-warning under the ruleset.

SCOPE: one line plus verification; do it as a standalone trivial commit (no gate chain needed beyond build + the pragma count; gate-exempt trivial class).
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
