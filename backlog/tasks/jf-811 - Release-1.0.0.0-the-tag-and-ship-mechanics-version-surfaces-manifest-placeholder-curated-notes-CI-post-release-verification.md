---
id: JF-811
title: >-
  Release 1.0.0.0: the tag-and-ship mechanics (version surfaces, manifest
  placeholder, curated notes, CI, post-release verification)
status: Done
assignee: []
created_date: '2026-10-07 08:43'
updated_date: '2026-10-18 17:12'
labels:
  - 'gate:device-round'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

HAZARD CLEARED (2026-10-07): the September-runway's parked local tag 1.0.0.0 (pointing at the stale 9b0fc105 tree) has been DELETED. It had never reached the remote (verified: zero hits on ls-remote), but with the version surfaces now at 1.0.0.0 a careless 'git push --tags' would have passed the CI's tag==version check and shipped September's code as the major release. The runbook's step 4 (git tag 1.0.0.0) now creates the tag fresh at the release commit; before executing it, verify 'git tag -l 1.0*' is empty.

1.0 BLOCKER ADDED (2026-10-08): JF-817 (the audiobook resume-slice live-edge death) enters the milestone ahead of the tag; found live by the device round. The code column is no longer complete until it lands.

1.0 BLOCKER ADDED (2026-10-08 night): JF-819 (the single-item HLS prewrite unwindowed: single-chapter/single-file book resumes ride it and hit the same live-edge death; promoted by the JF-817 gate-marker). The code column: JF-817 landed, JF-819 next.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED 2026-10-18 ~19:0x (maintainer's explicit 'vai col tag' after the JF-906 device verification closed the last blocker): the 1.0.0.0 release shipped. MECHANICS: pre-flight per the checklist (versions bumped on Directory.Build.props + build.yaml, catalog changelog written, manifest placeholder appended tail-first after the validator taught the list order, validate_versions PASS, local build 0 warnings + full suite 6050/6050 BOTH TFMs, icon verified); release commit 0582c806 + tag 1.0.0.0 pushed (selective tag push: the blanket --tags drag-along failed on the ancient backup/pre-rewrite tag which GitHub's pre-receive rejects - push main + the single tag ref instead). CI release run 39147560495 green in 3m55s. POST-RELEASE VERIFICATION ALL GREEN: release exists non-draft with BOTH zips (net10 1212567 B, net9 1212431 B); manifest commit 7ec9d538 on main carries the two real entries at indices 46 (net10, targetAbi 12.0.0.0, FIRST per the load-bearing order) and 47 (net9, 10.11.0.0), checksums byte-verified against locally-downloaded-and-hashed zips (acbb950a... / 37786ad0...), placeholder dropped (48 total); each zip contains 9 DLLs + icon.png; curated release notes applied (6281 bytes, docs/release/release_notes_1.0.0.0.md, covering September + October user-facing changes) replacing the 101-byte auto body. The release-runway memory is superseded: 1.0 SHIPPED. gate-exempt: version-surface mechanics under the maintainer's explicit order with the full local proof run; CI re-ran the entire battery on the tag.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
- [ ] #11 The tag pushed matches Directory.Build.props exactly
- [ ] #12 No 'placeholder' strings remain in manifest.json
- [ ] #13 The GitHub release body is NOT the bare auto-notes compare link
<!-- DOD:END -->
