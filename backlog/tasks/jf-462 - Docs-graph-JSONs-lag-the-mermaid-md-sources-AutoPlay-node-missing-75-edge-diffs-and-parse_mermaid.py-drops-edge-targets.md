---
id: JF-462
title: >-
  Docs graph JSONs lag the mermaid md sources (AutoPlay node missing, 75 edge
  diffs) and parse_mermaid.py drops edge targets
status: Done
assignee: []
created_date: '2026-09-03 06:03'
updated_date: '2026-09-03 11:04'
labels: []
dependencies: []
references:
  - docs-site/parse_mermaid.py
  - docs/graphs.json
  - docs-site/graphs.json
  - commit f2d1e17d (AutoPlay md edges)
  - JF-303 (commit 4481baaf) scoped-regen precedent
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-459 review-local gate (2026-09-03, reviewer c finding 1): the committed docs/graphs.json + docs-site/graphs.json lag the md sources in three diagram categories. A sandbox regeneration of docs-site/parse_mermaid.py over the current mds produces 75 edge diffs vs the committed JSONs: 34 in queue-radio (the committed diagrams lack the entire AutoPlay node and its edges, added to the mds by commit f2d1e17d 2026-06-02 without regenerating), 34 in library-browsing, 7 in playback-lifecycle (de-DE/fr-CA/fr-FR PlayVideo lines carry pre-ec417c59 labels from 2026-05-23). The docs-site explorer therefore renders stale flows.

Do NOT fix this by blindly re-running parse_mermaid.py: the script's current output also NULLS OUT resolved edge targets that the committed JSONs carry (e.g. "target": "Browse" becomes "target": null; verified empirically 2026-09-03). The task is to reconcile in BOTH directions: either fix parse_mermaid.py so a full regen is lossless (preferred: find where the committed targets came from, likely a newer parsing pass or a hand-edit convention, and restore that logic), or hand-merge the missing nodes/edges into the committed JSONs. The JF-303 precedent (commit 4481baaf) explicitly scoped this drift out as "separate docs sync": this is that sync.

Also update docs-site/data.json if the same drift affects its embedded mermaid strings (spot-check queue-radio ar-SA).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and pushed (commit 58d53ddf). Root cause found and fixed: the suspected parse_mermaid.py target-dropping was a stray closing bracket in all 17 library-browsing mermaid lines, introduced by ec417c59 (2026-05-23) and breaking both graph generation AND GitHub mermaid rendering for three months; the parser was never lossy for valid input.

Changes: the 17 md lines repaired; parse_mermaid.py gains a warnings channel printing the offending md line for every dropped edge endpoint (exempting only true curly-brace refs by SHAPE: a curly brace inside a square-bracket label, like {browse_category}, still warns, which is exactly the ec417c59 class; the initial containment-based exemption was caught by the review gate as P2@85 for silently swallowing that same class, and the fix is probe-verified: the exact pre-fix line warns, diamond refs stay silent, a HEAD-tree regen now prints 17 warnings); full regeneration copied to both byte-identical mirrors (byte-identical no-op for the 61 in-sync diagrams, proving losslessness; 41 drifted diagrams pick up the AutoPlay node and edges, the Browse label expansion, and 7 question-style PlayVideo edges); data.json's 58 embedded mermaid strings synced (they also carried a search-disambiguation lag from the JF-303 era); CLAUDE.md anti-pattern #11 flips the JF-459 do-not-regen rule to prefer re-running, with the history stated and the warning-exemption scope documented.

Numbers corrected from the task estimate: 78 measured diff entries pre-fix vs ~75 estimated; the playback-lifecycle drift was 7 new edges (10 locales were already current because JF-459 hand-updated those mirrors), not 10.

Docs/scripts only: no C#, no interaction models; validator at the 90-warning baseline, regen idempotent (md5-stable), mirrors byte-identical, both SPA consumers' field reads preserved verbatim (verified against docs/index.html + docs-site/index.html). No deploy (nothing to deploy).

Sub-threshold notes recorded (dismissed with reasons): a double-warning when both endpoints of one line fail (cosmetic, bounded); data.json has NO code consumer anywhere (both SPAs fetch graphs.json only) and is a third ungenerated mirror, a retirement-or-derive decision left to the maintainer (noted here rather than filed; the JF-462 task mandated syncing it and CLAUDE.md treats it as live).

Gates: /simplify + code-review combined in one pr-review-toolkit:code-reviewer pass (P2@85 applied with probe evidence; two overstated prose claims corrected; everything else verified clean with enumerated evidence, including the 61/102 no-op proof and the consumer field audit).
<!-- SECTION:FINAL_SUMMARY:END -->
