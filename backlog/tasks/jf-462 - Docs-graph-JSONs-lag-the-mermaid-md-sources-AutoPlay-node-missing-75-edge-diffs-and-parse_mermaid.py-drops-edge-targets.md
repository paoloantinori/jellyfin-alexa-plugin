---
id: JF-462
title: >-
  Docs graph JSONs lag the mermaid md sources (AutoPlay node missing, 75 edge
  diffs) and parse_mermaid.py drops edge targets
status: To Do
assignee: []
created_date: '2026-09-03 06:03'
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
