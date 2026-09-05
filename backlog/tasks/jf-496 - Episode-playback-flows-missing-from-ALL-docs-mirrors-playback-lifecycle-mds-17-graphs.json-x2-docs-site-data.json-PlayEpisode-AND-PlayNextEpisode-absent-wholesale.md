---
id: JF-496
title: >-
  Episode playback flows missing from ALL docs mirrors: playback-lifecycle mds
  (17), graphs.json (x2), docs-site/data.json (PlayEpisode AND PlayNextEpisode
  absent wholesale)
status: To Do
assignee: []
created_date: '2026-09-05 15:24'
labels:
  - documentation
  - tv
  - docs-mirrors
dependencies: []
references:
  - JF-494
  - JF-324
  - JF-493
  - 'CLAUDE.md anti-pattern #11 mirror rules'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-494's stale-mirror audit (2026-09-05): the docs mirrors are missing EPISODE PLAYBACK FLOWS wholesale, beyond any single task's samples. (1) docs/playback-lifecycle-<locale>.md, all 17 files: no PlayNextEpisode node/edges AND no PlayEpisodeIntent either (the JF-324 sibling) - episode playback is absent from every playback-lifecycle diagram while album/song/artist flows are all there. (2) docs/graphs.json + docs-site/graphs.json: zero episode-playback nodes; a parse_mermaid.py regen is a no-op until the mds gain the edges (per the CLAUDE.md JF-462 rule, prefer re-running the regen over hand-editing once the mds are fixed). (3) docs-site/data.json: same absence; update from the md sources in the same change (no generator exists for it). Add the episode flows to the 17 playback-lifecycle mds (Idle ->|sample| PlayEpisode / PlayNextEpisode edges, the NextUp resolution node, the VideoApp launch), then regenerate both graphs.json copies via python3 docs-site/parse_mermaid.py from the repo root, then sync docs-site/data.json from the mds. Pure docs + generated-artifact change.
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
