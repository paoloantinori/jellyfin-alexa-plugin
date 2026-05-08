---
id: JF-13
title: Update GitHub Actions CI/CD for .NET 9.0 and Jellyfin 10.11
status: Done
assignee: []
created_date: '2026-05-01 05:17'
updated_date: '2026-05-07 16:56'
labels:
  - ci-cd
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Update the existing GitHub Actions workflows for the 10.11 migration:

1. **release-build.yml**: Update .NET SDK from 8.x to 9.x, update publish path from net8.0 to net9.0
2. **dev-build.yml**: Same .NET SDK and path updates  
3. **add_release_to_manifest.py**: Read targetAbi from build.yaml instead of hardcoding "10.8.0.0". Read changelog from build.yaml too.
4. **release-build.yml**: Package only the DLLs listed in build.yaml artifacts (not the entire publish output)
5. **dev-build.yml**: Update to net9.0 paths
6. Ensure the ZIP structure matches what Jellyfin plugin catalog expects

Current state:
- Directory.Build.props has Version 0.2.0.0
- build.yaml has targetAbi 10.11.0.0, framework net9.0
- manifest.json already has the 0.2.0.0 entry with real checksum
- Workflows still reference net8.0 and .NET SDK 8.x
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed exhaustive research with 3 parallel agents investigating AMAZON.SearchQuery, custom slot types, and dialog strategies. Key findings: (1) Current slot architecture is already well-designed, (2) Amazon it-IT models include popular English artists but struggle with niche ones, (3) AMAZON.SearchQuery is per-utterance not per-intent limitation, (4) Music Skill API unavailable for custom skills, (5) Three improvement paths identified: utterance expansion + self-managed elicitation (quick win), catalog-based slot types (high impact), dynamic entities (session-scoped). Report saved to claudedocs/research_slot_types_mixed_language_2026-05-07.md
<!-- SECTION:FINAL_SUMMARY:END -->
