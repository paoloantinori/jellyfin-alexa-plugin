---
id: JF-849
title: >-
  The JF-564 Cannot* refusal family sits outside the ResponseStringsTests
  AllExpectedKeys ledger (JF-847's residual #1: only the two keys its branch
  speaks were added)
status: Done
assignee: []
created_date: '2026-10-09 20:03'
updated_date: '2026-10-09 23:41'
labels:
  - test-coverage
  - locales
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-847 -
    VideoApp-pause-is-state-aware-AudioPlayer.Stop-stops-the-video-iff-context-PlayerActivity-is-PLAYING-speak-VideoStoppedByVoice-vs-the-honest-cannot-pause-line.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the JF-847 reconciliation (the worker recorded it as residual (1) in the task file; landing it as its own row per the same-turn discipline).

JF-847 minted VideoStoppedByVoice in all 17 locales and added exactly TWO keys to the ResponseStringsTests AllExpectedKeys ledger: VideoStoppedByVoice and the pre-existing off-ledger CannotPauseVideoByVoice its branch speaks. The wider JF-564 refusal family the handler surface speaks is still OUTSIDE the ledger: CannotNavigateMusicByVoice, CannotNavigateLiveTvByVoice, CannotSetSleepTimerOverVideo, and any other Cannot* keys the VideoApp-gap arms speak (enumerate via grep over ResponseStrings.cs + handler usages before writing the ledger row). The AllExpectedKeys ledger (the JF-821 convention) is the completeness check that a key minted in all 17 locales cannot silently drift out of one; today these keys have no such guard.

Deliverable: enumerate the full Cannot* family actually spoken by handlers, verify each exists in all 17 locale files, add the missing rows to AllExpectedKeys in ResponseStringsTests.cs, and fix any locale gap the enumeration surfaces (a gap found here is a real bug: a handler speaking a key that is missing in some locale). Keep the ledger's per-key voice conventions (the JF-821 ledger rows carry the rationale comment).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MERGE NOTE (2026-10-10 02:45, orchestrator): merged as 1d6954f8 after independent verification. The worker's considered-and-deferred alternative (deriving the WHOLE AllExpectedKeys ledger from the en-US resource walk, a JF-821-mechanism refactor) is recorded here as the JF-822-convention record-only row: the ledger is a curated roster by convention, the acute Cannot* family is NOW mechanically derived via the new self-checking fact, and the generalization becomes due only if a second family shows the same drift shape.
<!-- SECTION:NOTES:END -->
