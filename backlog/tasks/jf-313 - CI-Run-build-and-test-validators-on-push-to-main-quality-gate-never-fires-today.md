---
id: JF-313
title: >-
  CI: Run build-and-test + validators on push to main (quality gate never fires
  today)
status: Done
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-07-18 08:15'
labels:
  - ci
  - quick-win
milestone: m-7
dependencies: []
references:
  - '.github/workflows/ci.yml:3'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`ci.yml` triggers only on `pull_request` to main + `workflow_dispatch` (verified 2026-07-12: `on: pull_request / workflow_dispatch`). Per CLAUDE.md this repo commits directly to main with no PRs, so the primary quality gate (Release build with -warnaserror + full test suite + validate-models/locales/versions/build-yaml) NEVER fires on the actual development workflow. Breakage is caught only at release tag time (release-build.yml) or by CodeQL push (security-only). This is a real gap for a repo whose test suite is its main safety net.

Fix: add `push: branches: [main]` to the build-and-test job trigger (keep PR + dispatch). Consider whether the advisory validators (validate-models) should stay non-blocking on push. Confirm the release path is unaffected. This is a low-cost, high-leverage change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 ci.yml build-and-test runs on direct pushes to main (in addition to PRs and workflow_dispatch)
- [x] #2 The full test suite and blocking validators run on push to main
- [x] #3 Release-build.yml (tag push) behavior is unchanged
- [x] #4 A trivial push to main is observed triggering the CI run (verified via gh run list)
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-18 07:51
---
2026-07-18 (autonomous): implemented — added `push: branches: [main]` to ci.yml triggers (commit f8e32c7, pushed to main). Verified AC#4: the push immediately triggered a CI run on main (run 29636444227, build-and-test + validators + CodeQL), confirming the gate now fires on direct pushes to main. release-build.yml untouched (AC#3). Run result pending; will close once it confirms green.
---

created: 2026-07-18 08:15
---
2026-07-18: CI run 29636444227 completed SUCCESS on main (build-and-test + all validators + CodeQL green). JF-313 fully delivered — the quality gate now fires on direct pushes to main.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented 2026-07-18 (autonomous): added `push: branches: [main]` to .github/workflows/ci.yml triggers (commit f8e32c7). Verified end-to-end — the push triggered CI on main (run 29636444227) which completed SUCCESS: build-and-test (Release -warnaserror + full suite) + validate-models/locales/versions/build-yaml + CodeQL all green. The quality gate now fires on the actual development workflow (direct pushes to main), closing the gap where breakage was caught only at release-tag time. release-build.yml untouched.
<!-- SECTION:FINAL_SUMMARY:END -->
