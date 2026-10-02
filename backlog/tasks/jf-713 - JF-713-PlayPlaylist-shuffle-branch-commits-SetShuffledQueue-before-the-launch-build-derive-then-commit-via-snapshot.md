---
id: JF-713
title: >-
  JF-713 - PlayPlaylist shuffle branch commits SetShuffledQueue before the launch
  build (derive-then-commit via shuffle snapshot)
status: To Do
assignee: []
created_date: '2026-10-02 15:05'
labels:
  - playback
  - queue
  - refusal-contract
dependencies:
  - JF-699
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-699 merge
(commit 65821618, finding 5 of 5). The PlayPlaylist shuffle branch (AlbumPlayService.cs
~980-1035) deliberately keeps SetShuffledQueue BEFORE the launch build - the in-code
comment names the reason (deriving the shuffled order before the build and shuffling
again at commit time would re-shuffle and disagree with the stored order) - so it is
the one site of the JF-699 reordered class where a refused launch still leaves written
state behind. Failure scenario: shuffled playlist start in seek mode with an empty
secret: SetShuffledQueue lands (the device queue is replaced with the shuffled order),
the builder then throws StreamTokenNotConfigured, and the device keeps a queue whose
contents never played while the session and continuation stay clean; a subsequent
resume or queue-browse answers the phantom shuffled queue. The playlist reorder was
otherwise documented as unpinnable end-to-end in JF-699 (GetManageableItems
non-virtual).

THE WORK: derive-then-commit via snapshot - compute the shuffled order ONCE into a
local, build the launch from it, and commit SetShuffledQueue with the SAME snapshot
after a successful build (the comment's re-shuffle objection is answered by reusing
the snapshot, not by re-deriving). Pin the refused-shuffle shape if the harness allows
(the JF-699 note says the playlist path is hard to pin end-to-end; a unit-level pin on
the service method with a throwing builder seam is acceptable evidence).
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
