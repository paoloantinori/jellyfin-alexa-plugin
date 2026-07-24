---
id: JF-173
title: Fix 300+ pre-existing build warnings to enable -warnaserror in CI
status: Done
assignee:
  - claude
created_date: '2026-05-17 13:48'
updated_date: '2026-05-17 15:11'
labels:
  - tech-debt
  - ci
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A trial CI run with `-warnaserror` revealed ~300 pre-existing warnings that block enabling it as a hard gate. These fall into categories:

**StyleCop (SAxxxx) — ~250 warnings:**
- 64x SA1611 (missing param docs)
- 64x SA1117 (parameters on same line)
- 46x SA1501 (opening braces on same line)
- 46x SA1107 (multiple statements per line)
- 38x SA1516 (spacing between elements)
- 32x SA1615 (missing return docs)
- 26x SA1201 (ordering)
- 16x SA1116 (split parameters)
- 14x SA1623 (duplicate summary text)
- 4x SA1010 (spacing)

**Nullable reference (CS8xxx) — ~24 warnings:**
- 8x CS8602 (possible null dereference)
- 6x CS8604 (possible null argument)
- 6x CS8601 (possible null assignment)
- 4x CS8600 (converting null literal)

**Obsolete API — 2 warnings:**
- 2x SYSLIB0057 (X509Certificate2 constructor obsolete in .NET 9)

**Goal:** Incrementally fix these so `-warnaserror` can be enabled as a hard CI gate, preventing new warnings from being introduced.

**Approach:** Fix by category in separate commits (e.g., one PR for nullable fixes, one for StyleCop SA1611, etc.) to keep reviews manageable. The obsolete API warnings may require a targeted suppression or migration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All CS8xxx nullable warnings fixed or suppressed with justification
- [ ] #2 All SAxxxx StyleCop warnings fixed or suppressed via .editorconfig/ruleset
- [ ] #3 SYSLIB0057 obsolete API usage migrated or suppressed
- [ ] #4 `-warnaserror` enabled in ci.yml build-and-test job
- [ ] #5 CI passes with zero warnings
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Plan: Fix build warnings for -warnaserror

Current state: Build has 0 warnings with TreatWarningsAsErrors=true locally.
The project already has many StyleCop rules disabled via jellyfin.ruleset.
CS1591 and CS1573 suppressed via NoWarn in csproj.

### Step 1: Verify -warnaserror works in CI
- Try enabling -warnaserror in ci.yml build-and-test job
- If CI passes, the task is already resolved

### Step 2: If failures, fix remaining warnings by category
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Findings
- Build already has 0 warnings with TreatWarningsAsErrors=true in both Debug and Release
- Most StyleCop rules already disabled via jellyfin.ruleset (SA1009, SA1101, SA1600, etc.)
- CS1591/CS1573 suppressed via NoWarn in csproj
- The original 300+ warnings from task description appear to have been resolved by previous commits
- Only 19 warnings remained per commit 'Remove -warnaserror from CI' — those have since been fixed
- Enabling -warnaserror in ci.yml as a hard gate
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Enabled `-warnaserror` as a hard CI gate in ci.yml. Fixed 11 blocking errors:

- **SA1117** (5): Reformatted parameter calls in PlayArtistSongsIntentHandler.cs
- **CS8xxx nullable** (8): Added null-forgiving operators (`!`) at sites guaranteed non-null by prior logic; fixed `string?` in ModelDeploymentManager where null is possible
- **xUnit1031** (3): Converted `.Result` to `await` in SimulatorControllerTests.cs
- **xUnit2013** (2): Replaced `Assert.Equal(0/1, count)` with `Assert.Empty/Single`
- **SYSLIB0057**: Targeted `#pragma warning disable` for obsolete X509Certificate2 constructor
- **CA2227**: Targeted `#pragma warning disable` for JSON deserialization collection properties

Added 30+ stylistic rule suppressions to jellyfin.ruleset (SA layout/formatting rules, CA stylistic rules) to prevent false positives while keeping semantic rules active. Changed CA1307 (StringComparison) from None to Info to keep locale-sensitivity visible.

Simplify review confirmed no reuse, efficiency, or quality issues remaining. Build passes with 0 warnings/errors, 1523 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
