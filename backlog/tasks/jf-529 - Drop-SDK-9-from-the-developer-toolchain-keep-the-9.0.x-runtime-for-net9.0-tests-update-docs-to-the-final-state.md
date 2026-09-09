---
id: JF-529
title: >-
  Drop SDK 9 from the developer toolchain (keep the 9.0.x runtime for net9.0
  tests); update docs to the final state
status: To Do
assignee: []
created_date: '2026-09-09 13:34'
updated_date: '2026-09-09 13:34'
labels:
  - toolchain
  - tech-debt
  - net10
dependencies: []
references:
  - JF-307
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Simplify the developer toolchain now that the dual-target build is proven under SDK 10 alone: uninstall the system SDK 9 (keeping the 9.0.x runtime for net9.0 test execution), update CLAUDE.md and the jf307 findings doc to the final toolchain state, and pin the rollback plan before touching anything. Rolling-deploy story: the shipping artifact stays net9.0 until the 10.11 support cutoff (product decision, tracked separately), so users see no change from this task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Developer toolchain: uninstall the system SDK 9 from this Fedora machine, keeping the 9.0.x runtime available for net9.0 test runs (verify: dotnet --list-sdks shows only 10.x, dotnet --list-runtimes shows 9.x + 10.x, the full net9.0 test suite runs green via the runtime)
- [ ] #2 CI (all three workflows): verify 9.0.x is installed as SDK or runtime per the same principle, and the both-TFM test matrix stays green after the change
- [ ] #3 Docs updated: CLAUDE.md build section and the jf307 findings doc reflect the final toolchain state (no SDK 9 references left that imply it is still needed)
- [ ] #4 Full suite green on both TFMs after the toolchain change (net10 with SDK 10; net9 via the 9.x runtime)
- [ ] #5 Rollback plan written down before the uninstall (reinstall command + version, per the backup-before-destructive rule)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Batch coordination (2026-09-09): this task composes with JF-307 Phase-2 (landed, merge af820dbb) and JF-530 (drop the 10.11 support line - a SEPARATE decision Paolo has NOT taken yet; do NOT conflate). What this task is NOT: it does not remove the net9.0 TFM from the csprojs (the shipping line stays net9.0), does not touch the Condition'd refs, and does not change what users receive. It only simplifies the DEVELOPER machine + CI installs per the finding that SDK 10 alone builds both TFMs (spike-verified: 0 warnings both TFMs under warnaserror). The 9.0.x RUNTIME stays installed locally for net9.0 test execution; if setup-dotnet cannot install a runtime-only on CI, the 9.0.x SDK stays in CI (document why).
<!-- SECTION:NOTES:END -->

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
