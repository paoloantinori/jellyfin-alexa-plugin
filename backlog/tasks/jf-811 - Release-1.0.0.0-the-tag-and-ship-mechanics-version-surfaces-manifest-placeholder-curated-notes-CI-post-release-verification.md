---
id: JF-811
title: >-
  Release 1.0.0.0: the tag-and-ship mechanics (version surfaces, manifest
  placeholder, curated notes, CI, post-release verification)
status: To Do
assignee: []
created_date: '2026-10-07 08:43'
labels: []
milestone: m-18
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The tag-and-ship umbrella for 1.0.0.0, tracking the project Release checklist end to end. Pre-conditions (the milestone's other tasks): the code essentials landed (JF-797/790/804/802), the device round green (JF-516's battery incl. the T1/T2 probes and the JF-778 AC#7 close evidence, JF-405's checklist, the verify battery legs).

MECHANICS (the CLAUDE.md Release section is the authority):
1. Version bumps to 1.0.0.0 in Directory.Build.props AND build.yaml; the changelog in build.yaml becomes the manifest entry (curate it: the audiobook wave, the confirm==ask unification, the warming gates, the cross-device follow-me position, the known limitations - stop/next wall, no scrubber, relative resume clock).
2. The manifest placeholder entry (checksum/changelog placeholders, correct sourceUrl, BOTH targetAbi entries with the 12.x line FIRST).
3. validate_versions.py green; full build+test locally; icon.jpg present.
4. Commit, tag 1.0.0.0, push main + tags; the CI release workflow builds both zips, computes checksums, updates the manifest back to main.
5. POST-RELEASE (mandatory): curated GitHub notes (user-facing prose, no code symbols; the auto-notes are a bare compare link for this direct-to-main repo), verify the served manifest shows 1.0.0.0 with real checksums, watch the catalog pickup (the issue #38 phantom-entry class must not recur: the placeholder MUST NOT reach the published manifest before the tag - the 2026-10-05 incident's lesson).
6. The release notes draft at docs/release/release_notes_1.0.0.0.md is the starting point; rewrite against what actually shipped since 0.12.1.0.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All three version surfaces show 1.0.0.0 and validate_versions.py passes
- [ ] #2 The manifest placeholder entries carry correct sourceUrl and targetAbi (net10 12.0.0.0 FIRST, then net9)
- [ ] #3 The GitHub release notes are curated user-facing prose (hundreds+ of bytes, sourced from build.yaml's changelog + docs/release/release_notes_1.0.0.0.md, known limitations honestly listed)
- [ ] #4 CI release workflow green: both zips built, checksums computed, manifest committed back
- [ ] #5 Post-release verification: served manifest carries the new version with real checksums, the catalog entry order correct (12.x first)
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
- [ ] #11 The tag pushed matches Directory.Build.props exactly
- [ ] #12 No 'placeholder' strings remain in manifest.json
- [ ] #13 The GitHub release body is NOT the bare auto-notes compare link
<!-- DOD:END -->

HAZARD CLEARED (2026-10-07): the September-runway's parked local tag 1.0.0.0 (pointing at the stale 9b0fc105 tree) has been DELETED. It had never reached the remote (verified: zero hits on ls-remote), but with the version surfaces now at 1.0.0.0 a careless 'git push --tags' would have passed the CI's tag==version check and shipped September's code as the major release. The runbook's step 4 (git tag 1.0.0.0) now creates the tag fresh at the release commit; before executing it, verify 'git tag -l 1.0*' is empty.
