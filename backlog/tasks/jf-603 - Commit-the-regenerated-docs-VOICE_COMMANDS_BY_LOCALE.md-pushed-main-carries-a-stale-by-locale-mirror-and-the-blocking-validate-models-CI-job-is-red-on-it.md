---
id: JF-603
title: >-
  Commit the regenerated docs/VOICE_COMMANDS_BY_LOCALE.md: pushed main carries a
  stale by-locale mirror and the blocking validate-models CI job is red on it
status: Done
assignee: []
created_date: '2026-09-20 19:15'
updated_date: '2026-09-20 19:56'
labels: []
dependencies: []
references:
  - commit 560b484c
  - docs/VOICE_COMMANDS_BY_LOCALE.md
  - scripts/generate_voice_reference.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review found that 560b484c (JF-600) committed the regenerated root VOICE_COMMANDS.md but NOT the by-locale mirror docs/VOICE_COMMANDS_BY_LOCALE.md: git show 560b484c:docs/VOICE_COMMANDS_BY_LOCALE.md | grep -c 'Crea playlist' = 0 while the committed it-IT model contains those new samples. The byte-exact regeneration sits UNCOMMITTED in the working tree (git status ' M docs/VOICE_COMMANDS_BY_LOCALE.md'; generate_voice_reference.py --check passes only against the working tree). The blocking validate-models CI job on HEAD's run failed with exactly the STALE line. The uncommitted file can be lost on checkout/reset (this mirror went stale twice before, JF-548-era). Fix: verify the working-tree file is the fresh regeneration (--check), commit it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 docs/VOICE_COMMANDS_BY_LOCALE.md on main matches what generate_voice_reference.py emits from the committed models (--check exits 0 on a clean clone)
- [ ] #2 The blocking validate-models CI job is green again (or the failure is proven unrelated by re-running the job)
- [ ] #3 Repo CLAUDE.md anti-pattern #11 mirror list is checked: no other mirror (VOICE_COMMANDS.md, docs mds, graphs.json, data.json, fixtures) is stale against the two commits
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit 595a8f40 "docs(voice): JF-603 commit the by-locale mirror regeneration"). The JF-600 push carried only VOICE_COMMANDS.md; the by-locale mirror sat regenerated-but-uncommitted in the tree and the blocking validate-models freshness job went red on the STALE line. Mirror rule honored: the generator writes both files, both committed; --check now passes. gate-exempt (byte-identical generator output, docs only).
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
<!-- DOD:END -->
